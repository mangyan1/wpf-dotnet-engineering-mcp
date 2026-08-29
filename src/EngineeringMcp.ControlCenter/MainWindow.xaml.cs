using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EngineeringMcp.ControlCenter;

/// <summary>
/// Core window plumbing: construction, layout discovery, theme switching, and the
/// status/log helpers every other partial relies on. Feature handlers live in the
/// sibling partials (McpServer, DevTests, Fixtures, VsCodePolicy).
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly FixedProcessRunner _runner = new();
    private readonly McpSelfTestService _mcpSelfTest = new();
    private readonly ObservableCollection<DevTestStep> _devSteps = [];
    private readonly string _probeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _backendToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private ProjectLayout _layout;
    private string _httpToken = string.Empty;
    private Process? _fixtureProcess;
    private Process? _backendProcess;
    private Process? _mcpServerProcess;
    private static readonly HttpClient RuntimeHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _activeDevTestCts;
    private bool _busy;
    private bool _systemThemeWatchEnabled;

    private readonly ObservableCollection<LogEntry> _logEntries = [];
    private System.Windows.Threading.DispatcherTimer? _clockTimer;
    private string _activeThemeMode = "Print";
    private bool _printDecoBuilt;
    private FrameworkElement? _printDecoration;

    public MainWindow()
    {
        InitializeComponent();
        InitializeBuildIdentity();
        DevTestGrid.ItemsSource = _devSteps;
        LatestList.ItemsSource = _devSteps;
        LogStream.ItemsSource = _logEntries;
        InitializeToolsPage();
        SessionCode.Text = "A7-" + (DateTime.Now.TimeOfDay.Minutes % 900).ToString("000");
        _clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TitleClock.Text = DateTime.UtcNow.ToString("HH:mm:ss");
        _clockTimer.Start();
        Loaded += async (_, _) =>
        {
            ApplyThemeMode("Print");
            StaleProcessCleanup.KillStale(AppendLog);
            await RefreshRuntimeStatusAsync();
            StartTopologyMonitoring();
            if (Environment.GetCommandLineArgs().Contains("--start-mcp", StringComparer.OrdinalIgnoreCase))
            {
                try { await StartMcpServerAsync(restart: false, CancellationToken.None); }
                catch (Exception ex)
                {
                    AppendLog("Automatic MCP start failed: " + ex.GetType().Name + ": " + ex.Message);
                    SetStatus("MCP server failed to start automatically. Use Repair MCP Server.");
                }
            }
        };

        try
        {
            _layout = ProjectLayout.Discover();
            _httpToken = GetOrCreateHttpToken();
            RuntimeHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _httpToken);
            RootPathText.Text = _layout.Root;
            PolicyPathText.Text = _layout.Policy;
            LoadPolicyPreview();
            ApplyRuntimeMode();
            RefreshStatus();
            AppendLog($"{_layout.ModeLabel} Control Center ready. Private probe and HTTP authentication tokens will not be displayed or logged.");
        }
        catch (Exception ex)
        {
            _layout = null!;
            RepositoryStatusText.Text = "● Repository not found";
            McpStatusText.Text = "● Unavailable";
            FixtureStatusText.Text = "● Unavailable";
            VsCodeStatusText.Text = "● Unavailable";
            SecurityStatusText.Text = "● Unavailable";
            AppendLog(ex.Message);
            SetStatus("Runtime discovery failed.");
        }
    }

    private void InitializeBuildIdentity()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var versionParts = informationalVersion.Split('+', 2, StringSplitOptions.TrimEntries);
        var version = versionParts[0];
        var revision = versionParts.Length == 2 ? versionParts[1] : string.Empty;
        var shortRevision = revision[..Math.Min(7, revision.Length)];

        VersionText.Text = string.IsNullOrWhiteSpace(shortRevision)
            ? $"VERSION {version}"
            : $"VERSION {version} / BUILD {shortRevision}";
        VersionText.ToolTip = $"Installed product version: {informationalVersion}";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag } &&
            MainTabs is not null && int.TryParse(tag, out var index))
        {
            MainTabs.SelectedIndex = index;
            if (_printDecoration is not null)
                PrintSheet.SetSecondaryPageLayout(_printDecoration, index > 0);
            // page transition (mockup glitch/blur-in, simplified to fade + slide)
            var slide = new System.Windows.Media.TranslateTransform(0, 9);
            MainTabs.RenderTransform = slide;
            MainTabs.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() });
            slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() });
        }
    }

    private void ThemeMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string mode })
            ApplyThemeMode(mode);
    }

    private void ApplyThemeMode(string mode)
    {
        if (VignetteLayer is null) return; // fired by the checked radio during InitializeComponent; Loaded re-applies

        if (_systemThemeWatchEnabled)
        {
            SystemThemeWatcher.UnWatch(this);
            _systemThemeWatchEnabled = false;
        }

        // Print is a dark cyanotype sheet; only Light runs on the WPF-UI light theme.
        var dark = mode switch
        {
            "Light" => false,
            "Dark" => true,
            "Print" => true,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark,
        };
        if (mode == "System")
        {
            ApplicationThemeManager.ApplySystemTheme();
            SystemThemeWatcher.Watch(this, WindowBackdropType.None, updateAccents: true);
            _systemThemeWatchEnabled = true;
        }
        else
        {
            // forceBackground:false — WPF-UI otherwise re-clobbers the window Background
            // asynchronously after Apply, stamping its charcoal over our palette (verified
            // via late-windowBg probe: #FF12283E -> #FF202020 at dispatcher idle).
            ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, false);
        }

        // swap the control-room palette; WPF-UI chrome follows via its own theme brushes.
        // Match by our token key — Wpf.Ui's theme dictionary also ends with "Dark.xaml".
        var dictionary = mode == "Print" ? "Print" : dark ? "Dark" : "Light";
        var assembly = typeof(App).Assembly.GetName().Name;
        var uri = new Uri($"pack://application:,,,/{assembly};component/Themes/{dictionary}.xaml");
        var merged = Application.Current.Resources.MergedDictionaries;
        var index = merged.IndexOf(merged.FirstOrDefault(d => d.Contains("BgBrush")));
        if (index < 0) return;
        var themeDict = new ResourceDictionary { Source = uri };
        merged[index] = themeDict;
        // re-add last so our overrides (ApplicationBackgroundBrush etc.) beat WPF-UI's own dict
        merged.RemoveAt(index);
        merged.Add(themeDict);
        _activeThemeMode = mode == "System" ? (dark ? "Dark" : "Light") : mode;

        // ApplicationThemeManager.Apply replaces the window Background with its own brush — reassert ours
        Background = (System.Windows.Media.Brush)FindResource("BgBrush");

        // backstop: WPF-UI may re-clobber the window Background asynchronously after Apply
        // (verified: windowBg #FF12283E at swap time, #FF202020 at dispatcher idle).
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Background is System.Windows.Media.SolidColorBrush sbb &&
                sbb.Color != ((System.Windows.Media.SolidColorBrush)FindResource("BgBrush")).Color)
                Background = (System.Windows.Media.Brush)FindResource("BgBrush");
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // shared gear decoration, print-only sheet furniture, and vignette strength follow the palette
        UpdatePrintDecoration();
        VignetteLayer.Opacity = mode == "Light" ? 0.15 : 0.35;
    }

    /// <summary>Keeps the gear train visible in every theme while reserving sheet furniture for Print.</summary>
    private void UpdatePrintDecoration()
    {
        if (!_printDecoBuilt)
        {
            _printDecoration = PrintSheet.Build();
            PrintSheet.SetSecondaryPageLayout(_printDecoration, MainTabs.SelectedIndex > 0);
            PrintDecoHost.Children.Add(_printDecoration);
            _printDecoBuilt = true;
        }
        PrintSheet.SetThemeMode(_printDecoration!, _activeThemeMode);
        PrintDecoHost.Visibility = Visibility.Visible;
    }

    /// <summary>XAML-facing endpoint constant (contracts assembly referenced from code only).</summary>
    public static string McpEndpoint => McpRuntimeDefaults.McpEndpoint;

    private bool ValidateLocalFiles()
    {
        var required = _layout.IsRepositoryMode
            ? new[] { _layout.Solution, _layout.HostProject, _layout.FixtureProject, _layout.Policy }
            : new[] { _layout.HostExecutable, _layout.Policy };
        var missing = required.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length == 0)
        {
            AppendLog("Local configuration files: PASS");
            return true;
        }

        foreach (var path in missing) AppendLog("MISSING: " + path);
        SetStatus("Required files are missing.");
        return false;
    }

    private void RefreshStatus()
    {
        if (_layout is null) return;
        var runtimeOk = _layout.IsRepositoryMode
            ? File.Exists(_layout.Solution) && File.Exists(_layout.HostProject)
            : File.Exists(_layout.HostExecutable) && File.Exists(_layout.Policy);
        var vscodeOk = IsVsCodeUserMcpInstalled();
        var securityOk = File.Exists(_layout.Policy) && File.Exists(_layout.SecurityDoc);

        RepositoryStatusText.Text = runtimeOk
            ? $"● {_layout.ModeLabel} ready"
            : "● Missing files";
        McpStatusText.Text = _mcpServerProcess is not null && !_mcpServerProcess.HasExited
            ? "● Running · HTTP"
            : McpStatusText.Text.StartsWith("● PASS", StringComparison.Ordinal) ? McpStatusText.Text : "● Stopped";
        VsCodeStatusText.Text = vscodeOk ? "● Ready" : "● Offline";
        SecurityStatusText.Text = securityOk ? "● Armed" : "● Policy missing";
        RefreshPolicyDiagnostics();
        FixtureStatusText.Text = _fixtureProcess is not null && !_fixtureProcess.HasExited
            ? $"● Running · PID {_fixtureProcess.Id}"
            : "● Stopped";
        VsCodeDetailText.Text = vscodeOk
            ? $"VS Code points to the shared local MCP service at {McpRuntimeDefaults.McpEndpoint}. Keep the MCP Server running while you use it."
            : "Not connected yet. Connect once so VS Code points to the shared local MCP service in every workspace.";
    }

    private static string GetOrCreateHttpToken()
    {
        var name = McpRuntimeDefaults.HttpTokenEnvironmentVariable;
        var token = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(token))
            token = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Environment.SetEnvironmentVariable(name, token, EnvironmentVariableTarget.User);
        }

        Environment.SetEnvironmentVariable(name, token, EnvironmentVariableTarget.Process);
        return token;
    }

    private void SetBusyUi(bool busy)
    {
        Dispatcher.Invoke(() =>
        {
            RunAllDevTestsButton.IsEnabled = !busy && _layout.SupportsDeveloperValidation;
            RunWpfEndToEndButton.IsEnabled = !busy && _layout.SupportsDeveloperValidation;
            CancelDevTestButton.IsEnabled = busy;
        });
    }

    private void ApplyRuntimeMode()
    {
        if (_layout.IsRepositoryMode) return;

        LaunchFixtureButton.IsEnabled = false;
        BuildSolutionButton.IsEnabled = false;
        RunCodeTestsButton.IsEnabled = false;
        CheckReadinessButton.IsEnabled = false;
        RunAllDevTestsButton.IsEnabled = false;
        RunWpfEndToEndButton.IsEnabled = false;
        OpenArtifactsButton.IsEnabled = false;
        WorkspaceScopeRadio.IsEnabled = false;
        GlobalScopeRadio.IsChecked = true;
        _mcpScope = "global";

        DevTestSummaryText.Text = "RUNTIME";
        DevValidationDescription.Text =
            "Repository builds, code tests, fixtures, and WPF end-to-end validation are available in Developer Mode. " +
            "Use Test MCP Server below to verify this packaged runtime.";
        RepairDescription.Text =
            "Stops the local service, verifies the packaged host and policy, and refreshes the VS Code connection.";
        LatestEmpty.Text = "// standalone runtime — use Test MCP Server for live protocol verification";
    }

    private bool EnsureReady()
    {
        if (_layout is not null) return true;
        AppendLog("Engineering MCP runtime could not be discovered.");
        SetStatus("Runtime not found. See Logs.");
        MainTabs.SelectedItem = LogsTab;
        return false;
    }

    private bool EnsureDeveloperMode(string operation)
    {
        if (!EnsureReady()) return false;
        if (_layout.SupportsDeveloperValidation) return true;

        AppendLog($"{operation} is available only in Developer Mode from a source checkout.");
        SetStatus($"{operation} requires Developer Mode.");
        return false;
    }

    private void AddStep(DevTestStep step)
    {
        Dispatcher.Invoke(() =>
        {
            _devSteps.Add(step);
            LatestEmpty.Visibility = Visibility.Collapsed;
            if (step.Status.Equals("FAIL", StringComparison.OrdinalIgnoreCase) ||
                step.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                FailureDetailText.Text = $"{step.Area} · {step.Test}: {step.Detail}";
            }
        });
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            ActivityLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            ActivityLog.ScrollToEnd();
            _logEntries.Insert(0, new LogEntry(message));
        });
    }

    private void SetStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            OperationStatusText.Text = status;
            FooterStatusText.Text = _busy
                ? (_layout?.IsRepositoryMode == true ? "Developer test running" : "Runtime operation running")
                : $"{_layout?.ModeLabel ?? "Unavailable"} mode";
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_systemThemeWatchEnabled)
            SystemThemeWatcher.UnWatch(this);
        _topologyTimer?.Stop();
        _activeDevTestCts?.Cancel();
        StopProcess(ref _mcpServerProcess);
        StopProcess(ref _fixtureProcess);
        StopProcess(ref _backendProcess);
        base.OnClosed(e);
    }
}
