using System.Windows;
using EngineeringMcp.Probe.Wpf;

namespace EngineeringMcpFixture;

public partial class App : Application
{
    private IDisposable? _probe;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Environment.GetEnvironmentVariable("ENGINEERING_MCP_PROBE_TOKEN") is { Length: >= 32 })
            _probe = WpfProbe.Start();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _probe?.Dispose();
        base.OnExit(e);
    }
}
