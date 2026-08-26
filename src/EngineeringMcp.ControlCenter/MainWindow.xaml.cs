using System.Collections.ObjectModel;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EngineeringMcp.Contracts;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EngineeringMcp.ControlCenter;

public partial class MainWindow : FluentWindow
{
    private readonly FixedProcessRunner _runner = new();
    private readonly McpSelfTestService _mcpSelfTest = new();
    private readonly ObservableCollection<DevTestStep> _devSteps = [];
    private readonly string _probeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private ProjectLayout _layout;
    private string _httpToken = string.Empty;
    private Process? _fixtureProcess;
    private Process? _backendProcess;
    private Process? _mcpServerProcess;
    private static readonly HttpClient RuntimeHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _activeDevTestCts;
    private bool _busy;
    private bool _systemThemeWatchEnabled;

    public MainWindow()
    {
        InitializeComponent();
        DevTestGrid.ItemsSource = _devSteps;
        Loaded += async (_, _) =>
        {
            ApplyThemeMode("System");
            await RefreshRuntimeStatusAsync();
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
            RootPathText.Text = _layout.Root;
            PolicyPathText.Text = _layout.Policy;
            RefreshStatus();
            AppendLog("Developer Control Center ready. Private probe and HTTP authentication tokens will not be displayed or logged.");
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
            SetStatus("Repository discovery failed.");
        }
    }


    private void ThemeSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not System.Windows.Controls.ComboBox comboBox ||
            comboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string mode)
            return;

        ApplyThemeMode(mode);
    }

    private void ApplyThemeMode(string mode)
    {
        if (_systemThemeWatchEnabled)
        {
            SystemThemeWatcher.UnWatch(this);
            _systemThemeWatchEnabled = false;
        }

        switch (mode)
        {
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);
                break;
            case "Dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
                _systemThemeWatchEnabled = true;
                break;
        }
    }

    private async void RunMcpServer_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady() || _busy) return;

        try
        {
            if (await StartMcpServerAsync(restart: false, CancellationToken.None))
                SetStatus("MCP server is running and available to Control Center and VS Code.");
        }
        catch (Exception ex)
        {
            McpStatusText.Text = "● Failed to start";
            AppendLog("Run MCP Server failed: " + ex.GetType().Name + ": " + ex.Message);
            SetStatus("MCP server failed to start. Use Repair MCP Server, then try again.");
        }
    }

    private async void StopMcpServer_Click(object sender, RoutedEventArgs e)
    {
        await StopMcpServerAsync();
        SetStatus("MCP server stopped.");
    }

    private async Task<bool> StartMcpServerAsync(bool restart, CancellationToken cancellationToken)
    {
        if (restart)
            await StopMcpServerAsync();
        else if (await EnsureMcpServerHealthyAsync(cancellationToken))
        {
            McpStatusText.Text = "● Running · HTTP";
            AppendLog("MCP server is already healthy at " + McpRuntimeDefaults.McpEndpoint);
            return true;
        }

        if (!File.Exists(_layout.HostExecutable))
        {
            AppendLog("MCP host executable is missing; building host first.");
            if (await RunDotNetAsync("Build MCP host", ["build", _layout.HostProject], cancellationToken) != 0)
                return false;
        }

        StopProcess(ref _mcpServerProcess);

        var startInfo = new ProcessStartInfo(_layout.HostExecutable)
        {
            WorkingDirectory = _layout.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(McpRuntimeDefaults.ListenUrl);

        var safeEnvironment = McpSelfTestService.CreateMinimalEnvironment(_layout, _probeToken, _httpToken);
        startInfo.Environment.Clear();
        foreach (var pair in safeEnvironment)
        {
            if (pair.Value is not null)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog("MCP host: " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog("MCP host: " + e.Data);
        };
        process.Exited += (_, _) =>
        {
            if (!ReferenceEquals(_mcpServerProcess, process)) return;
            Dispatcher.BeginInvoke(() => McpStatusText.Text = "● Stopped");
            AppendLog("MCP server process exited.");
        };

        if (!process.Start())
            throw new InvalidOperationException("Windows did not start the MCP host process.");

        _mcpServerProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        AppendLog($"MCP server process started (PID {process.Id}); waiting for {McpRuntimeDefaults.HealthEndpoint}.");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"MCP host exited before becoming healthy (exit code {process.ExitCode}).");
            if (await EnsureMcpServerHealthyAsync(cancellationToken))
            {
                McpStatusText.Text = "● Running · HTTP";
                AppendLog("MCP server healthy: " + McpRuntimeDefaults.McpEndpoint);
                return true;
            }
            await Task.Delay(150, cancellationToken);
        }

        StopProcess(ref _mcpServerProcess);
        McpStatusText.Text = "● Failed to start";
        AppendLog("MCP server did not become healthy within 10 seconds.");
        return false;
    }

    private async Task StopMcpServerAsync()
    {
        var stopped = StopProcess(ref _mcpServerProcess);

        if (stopped == 0)
        {
            var externalPid = await GetHealthyMcpProcessIdAsync(CancellationToken.None);
            if (externalPid is int pid && TryStopVerifiedHostProcess(pid))
            {
                stopped = 1;
                AppendLog($"Stopped previously orphaned MCP host PID {pid} after verifying its executable path.");
            }
        }

        AppendLog(stopped > 0 ? "MCP server process stopped." : "No verified Engineering MCP server process was running.");
        await Task.Delay(120);
        McpStatusText.Text = "● Stopped";
    }

    private bool TryStopVerifiedHostProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath) ||
                !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(_layout.HostExecutable), StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"Refused to stop PID {processId}: health endpoint process does not match the expected MCP host executable.");
                return false;
            }

            process.Kill(entireProcessTree: true);
            try { process.WaitForExit(2_000); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"Could not reclaim MCP host PID {processId}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> EnsureMcpServerHealthyAsync(CancellationToken cancellationToken)
        => await GetHealthyMcpProcessIdAsync(cancellationToken) is not null;

    private static async Task<int?> GetHealthyMcpProcessIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await RuntimeHttp.GetAsync(McpRuntimeDefaults.HealthEndpoint, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "ok", StringComparison.Ordinal) ||
                !root.TryGetProperty("server", out var server) || !string.Equals(server.GetString(), McpRuntimeDefaults.ServerName, StringComparison.Ordinal) ||
                !root.TryGetProperty("processId", out var processId) || !processId.TryGetInt32(out var pid))
                return null;

            return pid;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        if (!EnsureReady()) return;
        if (await EnsureMcpServerHealthyAsync(CancellationToken.None))
        {
            McpStatusText.Text = "● Running · HTTP";
            SetStatus("MCP server is available at " + McpRuntimeDefaults.McpEndpoint);
        }
    }

    private void ShowMcpLogs_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = LogsTab;
        SetStatus("Showing MCP server logs.");
    }

    private async void RunAllDevTests_Click(object sender, RoutedEventArgs e)
        => await RunDevOperationAsync("Full developer validation", RunAllDevTestsAsync);

    private async void RunMcpSelfTest_Click(object sender, RoutedEventArgs e)
        => await RunDevOperationAsync("MCP protocol self-test", RunMcpSelfTestAsync);

    private async void RunWpfEndToEnd_Click(object sender, RoutedEventArgs e)
        => await RunDevOperationAsync("WPF end-to-end test", RunWpfEndToEndAsync);

    private void CancelDevTest_Click(object sender, RoutedEventArgs e)
    {
        _activeDevTestCts?.Cancel();
        AppendLog("Cancellation requested for active developer test.");
        SetStatus("Cancelling…");
    }

    private async Task RunDevOperationAsync(string label, Func<CancellationToken, Task<bool>> operation)
    {
        if (!EnsureReady() || _busy) return;

        _busy = true;
        _activeDevTestCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        SetBusyUi(true);
        try
        {
            _devSteps.Clear();
            DevTestSummaryText.Text = "Running…";
            AppendLog($"=== {label.ToUpperInvariant()} ===");
            SetStatus(label + "…");

            var success = await operation(_activeDevTestCts.Token);
            DevTestSummaryText.Text = success ? "PASS" : "FAILED";
            SetStatus(success ? label + " passed." : label + " failed.");
            AppendLog($"=== {label}: {(success ? "PASS" : "FAIL")} ===");
        }
        catch (OperationCanceledException)
        {
            AddStep(new DevTestStep("Control Center", label, "CANCEL", "The operation was cancelled or exceeded its five-minute dev timeout."));
            DevTestSummaryText.Text = "CANCELLED";
            SetStatus(label + " cancelled.");
            AppendLog(label + ": CANCELLED");
        }
        catch (Exception ex)
        {
            AddStep(new DevTestStep("Control Center", label, "FAIL", ex.GetType().Name + ": " + ex.Message));
            DevTestSummaryText.Text = "FAILED";
            SetStatus(label + " failed.");
            AppendLog(label + " failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _activeDevTestCts.Dispose();
            _activeDevTestCts = null;
            _busy = false;
            SetBusyUi(false);
            RefreshStatus();
        }
    }

    private async Task<bool> RunAllDevTestsAsync(CancellationToken cancellationToken)
    {
        if (!ValidateLocalFiles()) return false;
        AddStep(new DevTestStep("Repository", "Required files", "PASS", "Solution, policy, host, fixture and MCP config files are present."));

        if (await RunDotNetAsync("Build solution", ["build", _layout.Solution], cancellationToken) != 0)
        {
            AddStep(new DevTestStep("Build", "dotnet build", "FAIL", "Solution build failed. See Logs tab."));
            return false;
        }
        AddStep(new DevTestStep("Build", "dotnet build", "PASS", "Solution compiled successfully."));

        if (await RunDotNetAsync("Run tests", ["test", _layout.Solution, "--no-build"], cancellationToken) != 0)
        {
            AddStep(new DevTestStep("Tests", "dotnet test", "FAIL", "One or more automated tests failed. See Logs tab."));
            return false;
        }
        AddStep(new DevTestStep("Tests", "unit + security + integration", "PASS", "Automated solution tests passed."));

        if (!await StartMcpServerAsync(restart: true, cancellationToken))
        {
            AddStep(new DevTestStep("Transport", "Start shared HTTP MCP service", "FAIL", "The local Streamable HTTP service did not become healthy."));
            return false;
        }
        AddStep(new DevTestStep("Transport", "Start shared HTTP MCP service", "PASS", McpRuntimeDefaults.McpEndpoint));

        if (!await RunMcpSelfTestCoreAsync(cancellationToken)) return false;
        if (!await _mcpSelfTest.RunStdioCompatibilitySmokeAsync(_layout, _probeToken, AddStep, AppendLog, cancellationToken)) return false;
        return await RunWpfEndToEndCoreAsync(buildFirst: false, cancellationToken);
    }

    private async Task<bool> RunMcpSelfTestAsync(CancellationToken cancellationToken)
    {
        if (!ValidateLocalFiles()) return false;
        if (!await StartMcpServerAsync(restart: false, cancellationToken))
        {
            AddStep(new DevTestStep("Transport", "Run MCP Server", "FAIL", "The shared HTTP service could not be started."));
            return false;
        }

        return await RunMcpSelfTestCoreAsync(cancellationToken);
    }

    private async Task<bool> RunMcpSelfTestCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _mcpSelfTest.RunProtocolSmokeAsync(
                _httpToken,
                AddStep,
                AppendLog,
                cancellationToken);

            McpStatusText.Text = report.Success
                ? $"● PASS · {report.ToolCount} tools"
                : "● FAILED";
            return report.Success;
        }
        catch
        {
            McpStatusText.Text = "● FAILED";
            throw;
        }
    }

    private async Task<bool> RunWpfEndToEndAsync(CancellationToken cancellationToken)
        => await RunWpfEndToEndCoreAsync(buildFirst: true, cancellationToken);

    private async Task<bool> RunWpfEndToEndCoreAsync(bool buildFirst, CancellationToken cancellationToken)
    {
        if (!ValidateLocalFiles()) return false;

        if (buildFirst && await RunDotNetAsync("Build solution", ["build", _layout.Solution], cancellationToken) != 0)
        {
            AddStep(new DevTestStep("Build", "WPF runtime prerequisites", "FAIL", "Build failed. See Logs tab."));
            return false;
        }

        if (!await StartMcpServerAsync(restart: false, cancellationToken))
        {
            AddStep(new DevTestStep("Transport", "Run MCP Server", "FAIL", "The shared HTTP service is unavailable."));
            return false;
        }

        var fixture = EnsureFixtureRunning();
        if (fixture is null)
        {
            AddStep(new DevTestStep("WPF fixture", "Launch", "FAIL", "Fixture could not be started."));
            return false;
        }

        AddStep(new DevTestStep("WPF fixture", "Launch", "PASS", $"Fixture PID {fixture.Id}; private probe token shared only through child process environments."));
        await WaitForFixtureWindowAsync(fixture, cancellationToken);

        try
        {
            if (!await EnsureMcpServerHealthyAsync(cancellationToken))
            {
                AddStep(new DevTestStep("Transport", "Shared HTTP MCP service", "FAIL", "Run MCP Server first or use Full Self Test."));
                return false;
            }

            var report = await _mcpSelfTest.RunWpfEndToEndAsync(
                fixture.Id,
                _httpToken,
                AddStep,
                AppendLog,
                cancellationToken);

            McpStatusText.Text = report.Success
                ? $"● PASS · {report.ToolCount} tools"
                : "● FAILED";
            FixtureStatusText.Text = report.Success
                ? $"● PASS · PID {fixture.Id}"
                : $"● Running · PID {fixture.Id}";
            return report.Success;
        }
        catch
        {
            McpStatusText.Text = "● FAILED";
            throw;
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady() || _busy) return;
        _busy = true;
        try { await RunDotNetAsync("Build solution", ["build", _layout.Solution], CancellationToken.None); }
        finally { _busy = false; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady() || _busy) return;
        _busy = true;
        try { await RunDotNetAsync("Run tests", ["test", _layout.Solution], CancellationToken.None); }
        finally { _busy = false; }
    }

    private async void RunReadiness_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady() || _busy) return;
        _busy = true;
        try
        {
            AppendLog("=== REPOSITORY READINESS ===");
            if (!ValidateLocalFiles()) return;
            if (await RunDotNetAsync("Build solution", ["build", _layout.Solution], CancellationToken.None) != 0) return;
            if (await RunDotNetAsync("Run tests", ["test", _layout.Solution, "--no-build"], CancellationToken.None) != 0) return;
            AppendLog("READINESS: PASS");
            SetStatus("Repository readiness passed.");
        }
        finally
        {
            _busy = false;
            RefreshStatus();
        }
    }

    private async Task<int> RunDotNetAsync(string label, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        AppendLog($"> {label}");
        SetStatus(label + "…");
        var code = await _runner.RunAsync("dotnet", arguments, _layout.Root, AppendLog, cancellationToken: cancellationToken);
        AppendLog($"{label}: {(code == 0 ? "PASS" : $"FAILED ({code})")}");
        SetStatus(code == 0 ? label + " passed." : label + " failed.");
        return code;
    }

    private void LaunchFixture_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady()) return;
        var process = EnsureFixtureRunning();
        if (process is not null)
            SetStatus("WPF fixture running.");
    }

    private void LaunchStack_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady()) return;
        var fixture = EnsureFixtureRunning();
        if (fixture is null) return;

        try
        {
            if (_backendProcess is null || _backendProcess.HasExited)
            {
                if (!File.Exists(_layout.AspNetFixtureExecutable))
                {
                    AppendLog("ASP.NET fixture executable is not built. Click Build solution or Run all dev tests first.");
                    SetStatus("Build the solution before launching the full stack.");
                    return;
                }

                _backendProcess?.Dispose();
                _backendProcess = _runner.StartDetached(_layout.AspNetFixtureExecutable, [], _layout.Root);
                AppendLog($"ASP.NET fixture started (PID {_backendProcess.Id}).");
            }

            SetStatus("WPF + ASP.NET fixtures running.");
        }
        catch (Exception ex)
        {
            AppendLog("Launch ASP.NET fixture failed: " + ex.Message);
            SetStatus("Full stack launch failed.");
        }
    }

    private Process? EnsureFixtureRunning()
    {
        if (_fixtureProcess is not null && !_fixtureProcess.HasExited)
        {
            FixtureStatusText.Text = $"● Running · PID {_fixtureProcess.Id}";
            return _fixtureProcess;
        }

        try
        {
            if (!File.Exists(_layout.FixtureExecutable))
            {
                AppendLog("WPF fixture executable is not built. Click Build solution or use Run WPF end-to-end.");
                SetStatus("Build the solution before launching the WPF fixture.");
                return null;
            }

            _fixtureProcess?.Dispose();
            _fixtureProcess = _runner.StartDetached(
                _layout.FixtureExecutable,
                [],
                _layout.Root,
                new Dictionary<string, string?> { ["ENGINEERING_MCP_PROBE_TOKEN"] = _probeToken });

            AppendLog($"WPF fixture started (PID {_fixtureProcess.Id}). Probe token remains in memory/child environment only.");
            FixtureStatusText.Text = $"● Running · PID {_fixtureProcess.Id}";
            return _fixtureProcess;
        }
        catch (Exception ex)
        {
            AppendLog("Launch WPF fixture failed: " + ex.Message);
            FixtureStatusText.Text = "● Launch failed";
            SetStatus("Fixture launch failed.");
            return null;
        }
    }

    private static async Task WaitForFixtureWindowAsync(Process fixture, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.Refresh();
            if (fixture.HasExited)
                throw new InvalidOperationException("WPF fixture exited before its main window became available.");
            if (fixture.MainWindowHandle != IntPtr.Zero)
                return;
            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("WPF fixture did not expose a main window within 15 seconds.");
    }

    private void StopFixtures_Click(object sender, RoutedEventArgs e)
    {
        var stopped = 0;
        stopped += StopProcess(ref _fixtureProcess);
        stopped += StopProcess(ref _backendProcess);
        AppendLog($"Stopped {stopped} fixture process(es).");
        FixtureStatusText.Text = "● Stopped";
        SetStatus("Fixtures stopped.");
    }

    private static int StopProcess(ref Process? process)
    {
        var current = process;
        process = null;
        if (current is null) return 0;

        try
        {
            if (current.HasExited)
                return 0;

            current.Kill(entireProcessTree: true);
            try { current.WaitForExit(2_000); } catch { }
            return 1;
        }
        catch
        {
            return 0;
        }
        finally
        {
            current.Dispose();
        }
    }

    private async void RepairMcpServer_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady() || _busy) return;
        _busy = true;
        SetBusyUi(true);
        try
        {
            AppendLog("=== REPAIR MCP SERVER ===");
            SetStatus("Repairing MCP server…");

            await StopMcpServerAsync();
            AppendLog("Stopped the shared MCP service before repair.");

            // VS Code registration points at the stable local HTTP endpoint. Refresh it after
            // repair in case an older stdio configuration is still present.
            if (IsVsCodeUserMcpInstalled())
                WriteVsCodeUserMcpConfiguration();

            var restore = await RunDotNetAsync(
                "Restore MCP server",
                ["restore", _layout.HostProject],
                CancellationToken.None);
            if (restore != 0)
            {
                SetStatus("Repair failed during restore. See Logs.");
                MainTabs.SelectedItem = LogsTab;
                return;
            }

            var build = await RunDotNetAsync(
                "Build MCP server",
                ["build", _layout.HostProject, "--no-restore"],
                CancellationToken.None);
            if (build != 0)
            {
                SetStatus("Repair failed during build. See Logs.");
                MainTabs.SelectedItem = LogsTab;
                return;
            }

            AppendLog("Repair MCP Server: PASS");
            SetStatus("MCP server repaired. Click Run MCP Server.");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            AppendLog("Repair MCP Server failed: " + ex.GetType().Name + ": " + ex.Message);
            SetStatus("Repair failed. See Logs.");
            MainTabs.SelectedItem = LogsTab;
        }
        finally
        {
            _busy = false;
            SetBusyUi(false);
        }
    }

    private void ConnectVsCode_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady()) return;
        try
        {
            var userConfig = WriteVsCodeUserMcpConfiguration();
            RefreshStatus();
            AppendLog("VS Code user-profile MCP configuration installed: " + userConfig);
            SetStatus("VS Code integration installed for all workspaces in this VS Code profile.");
        }
        catch (Exception ex)
        {
            AppendLog("Connect to VS Code failed: " + ex.GetType().Name + ": " + ex.Message);
            SetStatus("Could not install the VS Code user-profile MCP configuration. See Logs.");
            MainTabs.SelectedItem = LogsTab;
        }
    }

    private string WriteVsCodeUserMcpConfiguration()
    {
        // User-profile scope is intentional: a workspace-local .vscode/mcp.json only exists
        // while that repository is open. The engineering MCP is a developer tool that must
        // remain available when the user switches to ApexDrive or another authorized project.
        var configPath = GetVsCodeUserMcpConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        JsonObject document;
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            document = JsonNode.Parse(existing) as JsonObject
                ?? throw new InvalidDataException("VS Code user mcp.json is not a JSON object.");

            var backupPath = configPath + ".engineering-mcp.bak";
            File.Copy(configPath, backupPath, overwrite: true);
        }
        else
        {
            document = new JsonObject();
        }

        var servers = document["servers"] as JsonObject ?? new JsonObject();
        document["servers"] = servers;

        var server = new JsonObject
        {
            ["type"] = "http",
            ["url"] = McpRuntimeDefaults.McpEndpoint,
            ["headers"] = new JsonObject
            {
                ["Authorization"] = $"Bearer ${{env:{McpRuntimeDefaults.HttpTokenEnvironmentVariable}}}"
            }
        };

        servers[McpRuntimeDefaults.ServerName] = server;

        File.WriteAllText(
            configPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        return configPath;
    }

    private static string GetVsCodeUserMcpConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            throw new DirectoryNotFoundException("Windows application-data directory is unavailable.");

        var stableUser = Path.Combine(appData, "Code", "User");
        var insidersUser = Path.Combine(appData, "Code - Insiders", "User");

        if (Directory.Exists(stableUser))
            return Path.Combine(stableUser, "mcp.json");
        if (Directory.Exists(insidersUser))
            return Path.Combine(insidersUser, "mcp.json");

        // Default to stable's standard profile location. VS Code will create/use this path.
        return Path.Combine(stableUser, "mcp.json");
    }

    private static bool IsVsCodeUserMcpInstalled()
    {
        try
        {
            var configPath = GetVsCodeUserMcpConfigPath();
            if (!File.Exists(configPath)) return false;
            var document = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            var server = document?["servers"]?[McpRuntimeDefaults.ServerName] as JsonObject;
            return string.Equals(server?["type"]?.GetValue<string>(), "http", StringComparison.OrdinalIgnoreCase)
                && string.Equals(server?["url"]?.GetValue<string>(), McpRuntimeDefaults.McpEndpoint, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OpenVsCode_Click(object sender, RoutedEventArgs e)
    {
        OpenVsCodeUserMcpConfig();
    }

    private void OpenVsCodeUserMcpConfig()
    {
        try
        {
            var path = GetVsCodeUserMcpConfigPath();
            if (!File.Exists(path))
            {
                SetStatus("VS Code integration is not installed yet. Click Connect to VS Code first.");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            AppendLog("Opened the VS Code user-profile MCP configuration: " + path);
        }
        catch (Exception ex)
        {
            AppendLog("Could not open VS Code user MCP configuration: " + ex.Message);
            SetStatus("Could not open the VS Code MCP configuration. See Logs.");
        }
    }

    private void OpenMcpConfig_Click(object sender, RoutedEventArgs e) => OpenVsCodeUserMcpConfig();
    private void SelectPolicy_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Engineering MCP policy",
            Filter = "JSON policy (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Path.GetDirectoryName(_layout.Policy)
        };

        if (dialog.ShowDialog(this) is not true)
            return;

        var selected = Path.GetFullPath(dialog.FileName);
        _layout = _layout with { Policy = selected };
        Environment.SetEnvironmentVariable("ENGINEERING_MCP_POLICY", selected, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("ENGINEERING_MCP_POLICY", selected, EnvironmentVariableTarget.Process);
        PolicyPathText.Text = selected;
        RefreshStatus();
        AppendLog("Selected MCP policy: " + selected);
        SetStatus("MCP policy selected. Restart the MCP server to apply it.");
    }

    private void OpenPolicy_Click(object sender, RoutedEventArgs e) => OpenFile(_layout.Policy);
    private void OpenSecurity_Click(object sender, RoutedEventArgs e) => OpenFile(_layout.SecurityDoc);

    private void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            AppendLog("File not found: " + path);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ActivityLog.Text))
            Clipboard.SetText(ActivityLog.Text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ActivityLog.Clear();

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

    private bool ValidateLocalFiles()
    {
        var required = new[]
        {
            _layout.Solution, _layout.HostProject, _layout.FixtureProject,
            _layout.Policy
        };
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
        var repoOk = File.Exists(_layout.Solution) && File.Exists(_layout.HostProject);
        var vscodeOk = IsVsCodeUserMcpInstalled();
        var securityOk = File.Exists(_layout.Policy) && File.Exists(_layout.SecurityDoc);

        RepositoryStatusText.Text = repoOk ? "● Ready" : "● Missing files";
        McpStatusText.Text = _mcpServerProcess is not null && !_mcpServerProcess.HasExited
            ? "● Running · HTTP"
            : McpStatusText.Text.StartsWith("● PASS", StringComparison.Ordinal) ? McpStatusText.Text : "● Stopped";
        VsCodeStatusText.Text = vscodeOk ? "● Connected globally" : "● Not connected";
        SecurityStatusText.Text = securityOk ? "● Guardrails ready" : "● Policy missing";
        FixtureStatusText.Text = _fixtureProcess is not null && !_fixtureProcess.HasExited
            ? $"● Running · PID {_fixtureProcess.Id}"
            : "● Stopped";
        VsCodeDetailText.Text = vscodeOk
            ? $"VS Code points to the shared local MCP service at {McpRuntimeDefaults.McpEndpoint}. Keep the MCP Server running while you use it."
            : "Not connected yet. Connect once so VS Code points to the shared local MCP service in every workspace.";
    }

    private void SetBusyUi(bool busy)
    {
        Dispatcher.Invoke(() =>
        {
            RunAllDevTestsButton.IsEnabled = !busy;
            CancelDevTestButton.IsEnabled = busy;
        });
    }

    private bool EnsureReady()
    {
        if (_layout is not null) return true;
        AppendLog("Repository root could not be discovered.");
        SetStatus("Repository root not found. See Logs.");
        MainTabs.SelectedItem = LogsTab;
        return false;
    }

    private void AddStep(DevTestStep step)
    {
        Dispatcher.Invoke(() => _devSteps.Add(step));
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            ActivityLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            ActivityLog.ScrollToEnd();
        });
    }

    private void SetStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            OperationStatusText.Text = status;
            FooterStatusText.Text = _busy ? "Developer test running" : "Local dev mode";
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_systemThemeWatchEnabled)
            SystemThemeWatcher.UnWatch(this);
        _activeDevTestCts?.Cancel();
        StopProcess(ref _mcpServerProcess);
        StopProcess(ref _fixtureProcess);
        StopProcess(ref _backendProcess);
        base.OnClosed(e);
    }
}
