using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.ControlCenter;

/// <summary>
/// Pages beyond the core shell: Tools registry (static catalog mirroring the host's
/// 76 tools), Logs stream presentation, Integration helpers and value converters.
/// </summary>
public partial class MainWindow
{
    private string _mcpScope = "global";
    private string _toolFilter = "all";

    // ---- Tools registry ----------------------------------------------------

    public sealed record ToolRow(
        string Name, string Description, string Category,
        string Permission, string RiskText, Brush RiskForeground, Brush RiskBorder);

    // Static catalog mirrors the mockup's tool table; the authoritative set stays
    // with the host's CapabilityRegistry (ponytail: rebuild dynamically via tools/list when needed).
    private static readonly (string Name, string Desc, string Cat, string Risk)[] ToolsCatalog =
    [
        ("wpf_list_processes","List running WPF/WinUI processes","wpf","read"),
        ("wpf_attach","Attach UI Automation session to a process","wpf","read"),
        ("wpf_list_windows","List top-level windows of the attached process","wpf","read"),
        ("wpf_snapshot","Dump the UI tree as an accessibility snapshot","wpf","read"),
        ("wpf_find","Find nodes in the UI tree by selector","wpf","read"),
        ("wpf_query","Read properties of a UI node","wpf","read"),
        ("wpf_wait","Wait until a UI condition is met","wpf","read"),
        ("wpf_assert","Assert on UI state (text, enabled, visibility)","wpf","read"),
        ("wpf_click","Invoke or click a UI element","wpf","mutate"),
        ("wpf_type","Type text into a control","wpf","mutate"),
        ("wpf_select","Select an item in a combo/list","wpf","mutate"),
        ("wpf_toggle","Toggle a checkbox or switch","wpf","mutate"),
        ("wpf_expand","Expand an expander or tree node","wpf","mutate"),
        ("wpf_collapse","Collapse an expander or tree node","wpf","mutate"),
        ("wpf_scroll","Scroll a container into view","wpf","mutate"),
        ("wpf_focus","Set keyboard focus to a control","wpf","mutate"),
        ("wpf_screenshot","Capture the window as an image","wpf","read"),
        ("wpf_detach","Detach the UI Automation session","wpf","read"),
        ("wpf_probe","Handshake with the in-app probe endpoint","wpf","read"),
        ("wpf_wait_absent","Wait until a selected element is absent (metadata only)","wpf","read"),
        ("wpf_wait_hidden","Wait until a selected element is hidden (metadata only)","wpf","read"),
        ("wpf_wait_disabled","Wait until a selected element is disabled","wpf","read"),
        ("wpf_assert_exists","Assert that a selected element exists","wpf","read"),
        ("wpf_assert_not_exists","Assert that a selected element is absent","wpf","read"),
        ("wpf_assert_pattern","Assert an observed UI Automation pattern","wpf","read"),
        ("wpf_selector_audit","Audit selector stability without returning identifiers","wpf","read"),
        ("wpf_duplicate_automation_ids","Find duplicate IDs using fingerprints only","wpf","read"),
        ("wpf_control_inventory","Count UI Automation control types","wpf","read"),
        ("wpf_pattern_inventory","Count observed UI Automation patterns","wpf","read"),
        ("wpf_grid_summary","Summarize grid structure without cell values","wpf","read"),
        ("wpf_tree_summary","Summarize tree structure without node labels","wpf","read"),
        ("wpf_items_summary","Summarize item controls without item text","wpf","read"),
        ("wpf_accessibility_summary","Aggregate accessibility metadata without names","wpf","read"),
        ("wpf_window_state","Read window geometry/state without titles","wpf","read"),
        ("wpf_binding_info","Read binding metadata without bound values","wpf","read"),
        ("wpf_binding_errors","Read bounded binding error metadata","wpf","read"),
        ("wpf_command_state","Read command type/presence without invocation","wpf","read"),
        ("wpf_validation_summary","Count validation errors without messages/values","wpf","read"),
        ("wpf_datacontext_type","Read DataContext type only","wpf","read"),
        ("wpf_dispatcher_status","Read dispatcher status metadata","wpf","read"),
        ("wpfui_inspect","Inspect WPF-UI themed control metadata","wpf","read"),
        ("dotnet_runtime_info","Runtime version, GC mode, RID of a process","dotnet","read"),
        ("dotnet_counters","Read EventCounters from a live process","dotnet","read"),
        ("dotnet_gc_summary","GC heap summary and generations","dotnet","read"),
        ("dotnet_threads","List managed threads and their stacks","dotnet","read"),
        ("dotnet_modules","List loaded modules and versions","dotnet","read"),
        ("dotnet_exceptions","List recent exceptions","dotnet","read"),
        ("dotnet_trace_start","Start an EventPipe trace","dotnet","mutate"),
        ("dotnet_trace_stop","Stop a trace and return the capture","dotnet","mutate"),
        ("dotnet_capture_dump","Capture a crash/hang dump","dotnet","priv"),
        ("dotnet_analyze_dump","Analyze a dump with clrmd","dotnet","priv"),
        ("diagnose","Correlate recent app failures into a diagnosis","dotnet","read"),
        ("diagnose_click","Reproduce a UI failure by clicking the blamed element","dotnet","mutate"),
        ("aspnet_health","Probe a backend ASP.NET health endpoint","system","read"),
        ("aspnet_requests","List recent backend requests","system","read"),
        ("aspnet_exceptions","List recent backend exceptions","system","read"),
        ("source_inventory","Inventory the solution: projects, files, LOC","source","read"),
        ("source_read","Read a source file (policy-guarded)","source","read"),
        ("source_find_symbol","Find symbol definitions via Roslyn","source","read"),
        ("source_find_references","Find symbol references (exact)","source","read"),
        ("source_find_references_page","Find symbol references (paged)","source","read"),
        ("source_find_references_semantic","Find references via semantic model","source","read"),
        ("source_find_automation_id","Find the AutomationId for a XAML element","source","read"),
        ("source_find_binding","Locate XAML bindings for a property","source","read"),
        ("source_analyze_xaml","Analyze a XAML file for issues","source","read"),
        ("source_map_stacktrace","Map a runtime stack trace to source lines","source","read"),
        ("wpfui_audit_resources","Audit WPF-UI resource usage (themes, keys)","source","read"),
        ("a11y_audit","Audit accessibility tree (names, roles, contrast)","source","read"),
        ("gui_audit","Audit visual layout (clipping, overlap, spacing)","source","read"),
        ("ux_review","Heuristic UX review of a screen flow","source","read"),
        ("system_version","Server version and transport info","system","read"),
        ("system_health","Server self-health check","system","read"),
        ("system_capabilities","List capabilities and permissions","system","read"),
        ("system_permissions","Show effective policy for the caller","system","read"),
        ("system_policy_diagnostics","Explain policy denials and safe remediation","system","read"),
        ("system_tool_preflight","Check exact tool publication and authorization","system","read"),
    ];

    private static Brush ResBrush(string key) => (Brush)Application.Current.Resources[key];

    private static ToolRow ToToolRow((string Name, string Desc, string Cat, string Risk) t)
    {
        var (foreground, border, permission, riskText) = t.Risk switch
        {
            "read" => ("GreenBrush", "PillOkBorderBrush", "ui.read", "AUTO"),
            "mutate" => ("AmberBrush", "PillWarnBorderBrush", "ui.interact", "CONFIRM"),
            _ => ("RedBrush", "PillBadBorderBrush", "sensitive.diag", "RESTRICTED"),
        };
        return new ToolRow(t.Name, t.Desc, t.Cat, permission, riskText, ResBrush(foreground), ResBrush(border));
    }

    private void InitializeToolsPage()
    {
        ToolList.ItemsSource = ToolsCatalog.Select(ToToolRow).ToList();
    }

    private void ApplyToolFilter()
    {
        // chips fire IsChecked during InitializeComponent, before sibling elements exist
        if (ToolList is null || ToolSearch is null || ToolEmpty is null) return;
        var query = ToolSearch.Text.Trim();
        var rows = ToolsCatalog
            .Where(t => _toolFilter == "all" || t.Cat == _toolFilter)
            .Where(t => query.Length == 0 ||
                        t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        t.Desc.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(ToToolRow)
            .ToList();
        ToolList.ItemsSource = rows;
        ToolEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToolSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyToolFilter();

    private void ToolFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag })
        {
            _toolFilter = tag;
            ApplyToolFilter();
        }
    }

    // ---- Logs stream -------------------------------------------------------

    public sealed record LogEntry(string Time, string Source, string Message, Brush Brush)
    {
        public LogEntry(string message)
            : this(DateTime.Now.ToString("HH:mm:ss"), InferSource(message), message, MessageBrush(message)) { }

        // ponytail: keyword heuristic, tag explicitly once log messages carry a source
        private static string InferSource(string message)
        {
            var lower = message.ToLowerInvariant();
            if (lower.Contains("policy") || lower.Contains("guard") || lower.Contains("pii") ||
                lower.Contains("token") || lower.Contains("security"))
                return "security";
            if (lower.Contains("fixture") || lower.Contains("wpf end-to-end") || lower.Contains("uia"))
                return "wpf";
            if (lower.Contains("server") || lower.Contains("repair") || lower.Contains("endpoint") ||
                lower.Contains("transport") || lower.Contains("http"))
                return "server";
            return "mcp";
        }

        private static Brush MessageBrush(string message)
        {
            var lower = message.ToLowerInvariant();
            if (lower.Contains("pass") || lower.Contains("ready") || lower.Contains("healthy") ||
                lower.Contains("installed") || lower.Contains("repaired"))
                return ResBrush("GreenBrush");
            if (lower.Contains("fail") || lower.Contains("missing") || lower.Contains("unavailable") ||
                lower.Contains("not connected") || lower.Contains("cancelled"))
                return ResBrush("RedBrush");
            return ResBrush("Text2Brush");
        }
    }

    private string _logFilter = "";

    private void LogFilter_Click(object sender, RoutedEventArgs e)
    {
        if (LogStream is null) return; // fires during InitializeComponent
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag })
            _logFilter = tag;
        if (CollectionViewSource.GetDefaultView(_logEntries) is ICollectionView view)
        {
            view.Filter = o => _logFilter.Length == 0 || (o as LogEntry)?.Source == _logFilter;
            view.Refresh();
        }
    }

    // ---- Integration helpers ----------------------------------------------

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        // fires during InitializeComponent as well; guard against the half-built tree
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag } && McpJsonPathText is not null)
        {
            if (_layout is not null && !_layout.IsRepositoryMode && tag == "workspace")
            {
                GlobalScopeRadio.IsChecked = true;
                return;
            }

            _mcpScope = tag;
            McpJsonPathText.Text = tag == "workspace"
                ? "// <repo>\\.vscode\\mcp.json"
                : "// %APPDATA%\\Code\\User\\mcp.json";
        }
    }

    private void CopyEndpoint_Click(object sender, MouseButtonEventArgs e)
    {
        Clipboard.SetText(McpRuntimeDefaults.McpEndpoint);
        SetStatus("Endpoint copied to clipboard.");
    }

    private void CopyMcpConfig_Click(object sender, RoutedEventArgs e)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            servers = new Dictionary<string, object>
            {
                [McpRuntimeDefaults.ServerName] = new
                {
                    type = "http",
                    url = McpRuntimeDefaults.VsCodeMcpEndpoint,
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer ${{env:{McpRuntimeDefaults.HttpTokenEnvironmentVariable}}}"
                    }
                }
            }
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Clipboard.SetText(json);
        SetStatus("MCP configuration copied to clipboard.");
    }

    private void OpenArtifacts_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("Open developer-test artifacts")) return;

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DotNetEngineeringMcp", "selftest"));
        if (!Directory.Exists(root))
        {
            SetStatus("No self-test artifacts yet — run a validation first.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    private void RunAllDevTestsLink_Click(object sender, MouseButtonEventArgs e) => RunAllDevTests_Click(sender, new RoutedEventArgs());

    // ---- Policy preview ----------------------------------------------------

    private void LoadPolicyPreview()
    {
        try
        {
            if (File.Exists(_layout.Policy))
            {
                PolicyPreview.Text = File.ReadAllText(_layout.Policy);
                PolicyFileNameText.Text = Path.GetFileName(_layout.Policy);
            }
            else
            {
                PolicyPreview.Text = "// policy file not found";
            }
        }
        catch (Exception ex)
        {
            PolicyPreview.Text = "// could not read policy: " + ex.Message;
        }
    }
}

// ---- Value converters ------------------------------------------------------

/// <summary>Strips the leading status dot ("● ") for display use.</summary>
public sealed class TrimDotConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is string s ? s.TrimStart('●', ' ') : value;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Upper-cases for mono pill display.</summary>
public sealed class UpperConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is string s ? s.ToUpperInvariant() : value;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps check status text to the palette brush (green pass, red fail).</summary>
public sealed class StatusBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        if (text.Contains("PASS", StringComparison.OrdinalIgnoreCase)) return MainWindowRes.Green();
        if (text.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MISSING", StringComparison.OrdinalIgnoreCase)) return MainWindowRes.Red();
        return MainWindowRes.Dim();
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

internal static class MainWindowRes
{
    public static Brush Green() => (Brush)Application.Current.Resources["GreenBrush"];
    public static Brush Red() => (Brush)Application.Current.Resources["RedBrush"];
    public static Brush Dim() => (Brush)Application.Current.Resources["Text2Brush"];
}
