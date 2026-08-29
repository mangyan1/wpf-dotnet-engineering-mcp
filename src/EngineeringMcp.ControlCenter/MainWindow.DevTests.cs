using System.IO;
using System.Windows;
using EngineeringMcp.Contracts;
using Wpf.Ui.Controls;

namespace EngineeringMcp.ControlCenter;

/// <summary>Developer-test orchestration: isolated build/test runs, protocol smoke, WPF end-to-end.</summary>
public partial class MainWindow
{
    private async void RunAllDevTests_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("Full developer validation")) return;
        await RunDevOperationAsync("Full developer validation", RunAllDevTestsAsync);
    }

    private async void RunMcpSelfTest_Click(object sender, RoutedEventArgs e)
        => await RunDevOperationAsync("MCP protocol self-test", RunMcpSelfTestAsync);

    private async void RunWpfEndToEnd_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("WPF end-to-end test")) return;
        await RunDevOperationAsync("WPF end-to-end test", RunWpfEndToEndAsync);
    }

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

        return await RunIsolatedValidationAsync(
            runAutomatedTests: true,
            async (runtimeLayout, token) =>
            {
                if (!await StartMcpServerAsync(restart: false, token, runtimeLayout))
                {
                    AddStep(new DevTestStep("Transport", "Start isolated HTTP MCP service", "FAIL", "The isolated Streamable HTTP service did not become healthy."));
                    return false;
                }
                AddStep(new DevTestStep("Transport", "Start isolated HTTP MCP service", "PASS", McpRuntimeDefaults.McpEndpoint));

                if (!await RunMcpSelfTestCoreAsync(token)) return false;
                if (!await _mcpSelfTest.RunStdioCompatibilitySmokeAsync(runtimeLayout, _probeToken, AddStep, AppendLog, token)) return false;
                var backend = EnsureBackendRunning(runtimeLayout);
                if (backend is null)
                {
                    AddStep(new DevTestStep("ASP.NET fixture", "Launch", "FAIL", "Fixture could not be started."));
                    return false;
                }
                AddStep(new DevTestStep("ASP.NET fixture", "Launch", "PASS", $"Fixture PID {backend.Id}; private adapter token shared only through child process environments."));
                if (!await _mcpSelfTest.RunAspNetEndToEndAsync(backend.Id, _httpToken, AddStep, AppendLog, token)) return false;
                return await RunWpfEndToEndCoreAsync(runtimeLayout, token);
            },
            cancellationToken);
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
    {
        if (!ValidateLocalFiles()) return false;

        return await RunIsolatedValidationAsync(
            runAutomatedTests: false,
            async (runtimeLayout, token) =>
            {
                if (!await StartMcpServerAsync(restart: false, token, runtimeLayout))
                {
                    AddStep(new DevTestStep("Transport", "Start isolated HTTP MCP service", "FAIL", "The isolated Streamable HTTP service did not become healthy."));
                    return false;
                }

                return await RunWpfEndToEndCoreAsync(runtimeLayout, token);
            },
            cancellationToken);
    }

    private async Task<bool> RunWpfEndToEndCoreAsync(ProjectLayout runtimeLayout, CancellationToken cancellationToken)
    {
        if (!await StartMcpServerAsync(restart: false, cancellationToken, runtimeLayout))
        {
            AddStep(new DevTestStep("Transport", "Run MCP Server", "FAIL", "The shared HTTP service is unavailable."));
            return false;
        }

        var fixture = EnsureFixtureRunning(runtimeLayout);
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

    private async Task<bool> RunIsolatedValidationAsync(
        bool runAutomatedTests,
        Func<ProjectLayout, CancellationToken, Task<bool>>? runtimeValidation,
        CancellationToken cancellationToken)
    {
        var artifacts = SelfTestArtifactLayout.Create();
        var runtimeLayout = artifacts.ApplyTo(_layout);
        var result = false;
        var cleanupSucceeded = true;

        try
        {
            async Task<bool> ExecuteAsync()
            {
                var buildArguments = new[]
                {
                    "build", _layout.Solution,
                    "--configuration", SelfTestArtifactLayout.Configuration,
                    "--artifacts-path", artifacts.Root
                };
                if (await RunDotNetAsync("Build isolated validation artifacts", buildArguments, cancellationToken) != 0)
                {
                    AddStep(new DevTestStep("Build", "isolated dotnet build", "FAIL", "Solution build failed. See Logs tab."));
                    return false;
                }
                AddStep(new DevTestStep("Build", "isolated dotnet build", "PASS", "Solution compiled outside the running application's output directory."));

                if (runAutomatedTests)
                {
                    var testArguments = new[]
                    {
                        "test", _layout.Solution,
                        "--configuration", SelfTestArtifactLayout.Configuration,
                        "--no-build",
                        "--artifacts-path", artifacts.Root
                    };
                    var testEnvironment = new Dictionary<string, string?>
                    {
                        [McpRuntimeDefaults.RepositoryRootEnvironmentVariable] = _layout.Root,
                        [McpRuntimeDefaults.ArtifactsPathEnvironmentVariable] = artifacts.Root
                    };
                    if (await RunDotNetAsync("Run isolated tests", testArguments, cancellationToken, testEnvironment) != 0)
                    {
                        AddStep(new DevTestStep("Tests", "isolated dotnet test", "FAIL", "One or more automated tests failed. See Logs tab."));
                        return false;
                    }
                    AddStep(new DevTestStep("Tests", "unit + security + integration", "PASS", "Automated tests passed against the isolated build."));
                }

                return runtimeValidation is null ||
                    await RunWithIsolatedRuntimeAsync(runtimeLayout, runtimeValidation, cancellationToken);
            }

            result = await ExecuteAsync();
        }
        finally
        {
            cleanupSucceeded = artifacts.TryDelete(out var cleanupError);
            if (cleanupSucceeded)
            {
                AppendLog("Isolated validation artifacts removed.");
            }
            else
            {
                AddStep(new DevTestStep("Cleanup", "isolated artifacts", "FAIL", cleanupError ?? "Artifact cleanup failed."));
                AppendLog("Isolated validation artifact cleanup failed: " + cleanupError);
            }
        }

        return result && cleanupSucceeded;
    }

    private async Task<bool> RunWithIsolatedRuntimeAsync(
        ProjectLayout runtimeLayout,
        Func<ProjectLayout, CancellationToken, Task<bool>> runtimeValidation,
        CancellationToken cancellationToken)
    {
        var serverWasHealthy = await EnsureMcpServerHealthyAsync(cancellationToken);
        var fixtureWasRunning = IsRunning(_fixtureProcess);
        var backendWasRunning = IsRunning(_backendProcess);
        var result = false;
        var restored = true;

        StopProcess(ref _fixtureProcess);
        StopProcess(ref _backendProcess);
        FixtureStatusText.Text = "● Stopped";
        await StopMcpServerAsync();

        try
        {
            result = await runtimeValidation(runtimeLayout, cancellationToken);
        }
        finally
        {
            StopProcess(ref _fixtureProcess);
            StopProcess(ref _backendProcess);
            FixtureStatusText.Text = "● Stopped";
            await StopMcpServerAsync();

            if (serverWasHealthy)
            {
                try
                {
                    restored = await StartMcpServerAsync(restart: false, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    restored = false;
                    AppendLog("Could not restore the pre-test MCP server: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (fixtureWasRunning && EnsureFixtureRunning() is null)
                restored = false;
            if (backendWasRunning && EnsureBackendRunning() is null)
                restored = false;
        }

        if (!restored)
            AddStep(new DevTestStep("Control Center", "Restore previous runtime", "FAIL", "The validation completed, but the previous local runtime state could not be restored."));

        return result && restored;
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("Build solution") || _busy) return;
        _busy = true;
        try { await RunIsolatedValidationAsync(runAutomatedTests: false, runtimeValidation: null, cancellationToken: CancellationToken.None); }
        finally { _busy = false; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("Run code tests") || _busy) return;
        _busy = true;
        try { await RunIsolatedValidationAsync(runAutomatedTests: true, runtimeValidation: null, cancellationToken: CancellationToken.None); }
        finally { _busy = false; }
    }

    private async void RunReadiness_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("Repository readiness") || _busy) return;
        _busy = true;
        try
        {
            AppendLog("=== REPOSITORY READINESS ===");
            if (!ValidateLocalFiles()) return;
            if (!await RunIsolatedValidationAsync(runAutomatedTests: true, runtimeValidation: null, cancellationToken: CancellationToken.None)) return;
            AppendLog("READINESS: PASS");
            SetStatus("Repository readiness passed.");
        }
        finally
        {
            _busy = false;
            RefreshStatus();
        }
    }

    private async Task<int> RunDotNetAsync(
        string label,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        AppendLog($"> {label}");
        SetStatus(label + "…");
        var code = await _runner.RunAsync("dotnet", arguments, _layout.Root, AppendLog, environment, cancellationToken);
        AppendLog($"{label}: {(code == 0 ? "PASS" : $"FAILED ({code})")}");
        SetStatus(code == 0 ? label + " passed." : label + " failed.");
        return code;
    }
}
