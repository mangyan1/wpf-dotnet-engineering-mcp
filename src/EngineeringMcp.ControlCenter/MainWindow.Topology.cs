using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EngineeringMcp.Contracts;
using ShapePath = System.Windows.Shapes.Path;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace EngineeringMcp.ControlCenter;

/// <summary>Live state and motion for the Home-page runtime topology.</summary>
public partial class MainWindow
{
    private readonly Dictionary<ShapePath, bool> _topologyConnectionStates = [];
    private DispatcherTimer? _topologyTimer;
    private bool _topologyRefreshInProgress;

    private void StartTopologyMonitoring()
    {
        if (_topologyTimer is not null) return;

        _topologyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topologyTimer.Tick += async (_, _) => await RefreshTopologyAsync();
        _topologyTimer.Start();
    }

    private async Task RefreshTopologyAsync()
    {
        if (_topologyRefreshInProgress || _layout is null) return;

        _topologyRefreshInProgress = true;
        try
        {
            UpdateTopologyVisual(await GetMcpHealthAsync(CancellationToken.None));
        }
        finally
        {
            _topologyRefreshInProgress = false;
        }
    }

    private void UpdateTopologyVisual(McpHealthSnapshot? health)
    {
        if (_layout is null || TopologyServerNode is null) return;

        var serverLive = health is not null;
        var vsCodeConfigured = IsVsCodeUserMcpInstalled();
        var vsCodeMarkerInstalled = HasVsCodeClientMarkerInstalled();
        var vsCodeLive = serverLive && health!.VsCodeActive;
        var fixtureLive = IsProcessRunning(_fixtureProcess);
        var policyReady = File.Exists(_layout.Policy) && File.Exists(_layout.SecurityDoc);

        var amber = (Brush)FindResource("AmberBrush");
        var green = (Brush)FindResource("GreenBrush");
        var cyan = (Brush)FindResource("CyanBrush");
        var text2 = (Brush)FindResource("Text2Brush");
        var text3 = (Brush)FindResource("Text3Brush");
        var hair = (Brush)FindResource("HairBrush");
        var panel = (Brush)FindResource("Panel2Brush");
        var raised = (Brush)FindResource("RaiseBrush");
        var amberGlow = (Brush)FindResource("AmberGlowBrush");

        SetConnectionState(ControlCenterConnection, serverLive, true, amber, text3, hair);
        SetConnectionState(VsCodeConnection, vsCodeLive, vsCodeConfigured, green, cyan, hair);
        SetConnectionState(FixtureConnection, serverLive && fixtureLive, fixtureLive, green, text3, hair);
        SetConnectionState(PolicyConnection, serverLive && policyReady, policyReady, green, text3, hair);

        SetNodeState(TopologyControlNode, true, text2, raised, panel, hair);
        SetNodeState(TopologyServerNode, serverLive, amber, amberGlow, panel, hair);
        SetNodeState(TopologyVsCodeNode, vsCodeLive, green, raised, panel, vsCodeConfigured ? cyan : hair);
        if (vsCodeConfigured && !vsCodeLive)
            TopologyVsCodeNode.Opacity = 0.9;
        SetNodeState(TopologyFixtureNode, fixtureLive, green, raised, panel, hair);
        SetNodeState(TopologyPolicyNode, policyReady, green, raised, panel, hair);

        TopologyServerDetail.Text = serverLive ? ":8765 · LIVE" : ":8765 · OFF";
        TopologyVsCodeLabel.Text = vsCodeLive
            ? "VS CODE · LIVE"
            : !vsCodeConfigured ? "VS CODE · OFF"
            : vsCodeMarkerInstalled ? "VS CODE · READY" : "VS CODE · UPDATE";
        TopologyFixtureLabel.Text = fixtureLive ? "WPF · LIVE" : "WPF · IDLE";
        TopologyPolicyLabel.Text = policyReady ? "POLICY · ARMED" : "POLICY · MISSING";

        McpStatusText.Text = serverLive ? "● Running · HTTP" : "● Stopped";
        VsCodeStatusText.Text = vsCodeLive
            ? "● Live"
            : !vsCodeConfigured ? "● Offline"
            : vsCodeMarkerInstalled ? "● Ready" : "● Update";
        VsCodeDetailText.Text = vsCodeLive
            ? $"Live VS Code MCP traffic detected at {McpRuntimeDefaults.McpEndpoint}."
            : !vsCodeConfigured
                ? "Not connected yet. Connect once so VS Code points to the shared local MCP service."
                : !vsCodeMarkerInstalled
                    ? "Reconnect VS Code once to install live-activity detection, then reload VS Code."
                    : "VS Code is configured. The connection turns Live when VS Code sends MCP traffic.";

        TopologyVsCodeNode.ToolTip = vsCodeLive && health!.LastVsCodeActivityUtc is DateTimeOffset lastSeen
            ? $"VS Code MCP activity seen at {lastSeen.ToLocalTime():T}."
            : TopologyVsCodeLabel.Text;
    }

    private void SetConnectionState(
        ShapePath connection,
        bool active,
        bool ready,
        Brush activeBrush,
        Brush readyBrush,
        Brush inactiveBrush)
    {
        connection.Stroke = active ? activeBrush : ready ? readyBrush : inactiveBrush;
        connection.Opacity = active ? 0.95 : ready ? 0.72 : 0.22;

        if (_topologyConnectionStates.TryGetValue(connection, out var wasActive) && wasActive == active)
            return;

        _topologyConnectionStates[connection] = active;
        if (!active)
        {
            connection.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            connection.StrokeDashOffset = 0;
            return;
        }

        connection.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation
        {
            From = 0,
            To = -14,
            Duration = TimeSpan.FromSeconds(0.9),
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private static void SetNodeState(
        ShapeRectangle node,
        bool active,
        Brush activeStroke,
        Brush activeFill,
        Brush inactiveFill,
        Brush inactiveStroke)
    {
        node.Fill = active ? activeFill : inactiveFill;
        node.Stroke = active ? activeStroke : inactiveStroke;
        node.Opacity = active ? 1 : 0.72;
    }

    private static bool IsProcessRunning(Process? process)
    {
        try { return process is not null && !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }
}
