using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using EngineeringMcp.Contracts;
using Wpf.Ui.Controls;

namespace EngineeringMcp.ControlCenter;

/// <summary>Shared MCP host process lifecycle: start, health verification, stop, repair.</summary>
public partial class MainWindow
{
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

    private async Task<bool> StartMcpServerAsync(
        bool restart,
        CancellationToken cancellationToken,
        ProjectLayout? runtimeLayout = null)
    {
        var layout = runtimeLayout ?? _layout;

        if (restart)
            await StopMcpServerAsync();
        else if (await EnsureMcpServerHealthyAsync(cancellationToken))
        {
            McpStatusText.Text = "● Running · HTTP";
            AppendLog("MCP server is already healthy at " + McpRuntimeDefaults.McpEndpoint);
            return true;
        }

        if (!File.Exists(layout.HostExecutable))
        {
            if (runtimeLayout is not null || !layout.IsRepositoryMode)
            {
                AppendLog(layout.IsRepositoryMode
                    ? "Isolated MCP host executable is missing after the validation build."
                    : "Packaged MCP host executable is missing. Reinstall or extract the complete application package.");
                return false;
            }

            AppendLog("MCP host executable is missing; building host first.");
            if (await RunDotNetAsync("Build MCP host", ["build", _layout.HostProject], cancellationToken) != 0)
                return false;
        }

        StaleProcessCleanup.KillStale(AppendLog);
        StopProcess(ref _mcpServerProcess);

        var startInfo = new ProcessStartInfo(layout.HostExecutable)
        {
            WorkingDirectory = layout.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(McpRuntimeDefaults.ListenUrl);

        var safeEnvironment = McpSelfTestService.CreateMinimalEnvironment(layout, _probeToken, _httpToken, _backendToken);
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
        => await GetMcpHealthAsync(cancellationToken) is not null;

    private static async Task<int?> GetHealthyMcpProcessIdAsync(CancellationToken cancellationToken)
        => (await GetMcpHealthAsync(cancellationToken))?.ProcessId;

    private static async Task<McpHealthSnapshot?> GetMcpHealthAsync(CancellationToken cancellationToken)
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

            var vsCodeActive = root.TryGetProperty("vsCodeActive", out var active) && active.ValueKind == JsonValueKind.True;
            DateTimeOffset? lastVsCodeActivityUtc = null;
            if (root.TryGetProperty("lastVsCodeActivityUtc", out var lastActivity) &&
                lastActivity.ValueKind == JsonValueKind.String &&
                lastActivity.TryGetDateTimeOffset(out var timestamp))
            {
                lastVsCodeActivityUtc = timestamp;
            }

            return new McpHealthSnapshot(pid, vsCodeActive, lastVsCodeActivityUtc);
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
        var health = await GetMcpHealthAsync(CancellationToken.None);
        UpdateTopologyVisual(health);
        if (health is not null)
        {
            McpStatusText.Text = "● Running · HTTP";
            SetStatus("MCP server is available at " + McpRuntimeDefaults.McpEndpoint);
        }
    }

    private sealed record McpHealthSnapshot(int ProcessId, bool VsCodeActive, DateTimeOffset? LastVsCodeActivityUtc);

    private void ShowMcpLogs_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = LogsTab;
        SetStatus("Showing MCP server logs.");
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
                WriteVsCodeMcpConfiguration(GetVsCodeUserMcpConfigPath());

            if (!_layout.IsRepositoryMode)
            {
                if (!ValidateLocalFiles())
                {
                    SetStatus("Repair failed: packaged runtime files are missing.");
                    MainTabs.SelectedItem = LogsTab;
                    return;
                }

                AppendLog("Packaged host and policy verified. No source restore or rebuild is required.");
                SetStatus("Packaged MCP server verified. Click Run MCP Server.");
                RefreshStatus();
                return;
            }

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
}
