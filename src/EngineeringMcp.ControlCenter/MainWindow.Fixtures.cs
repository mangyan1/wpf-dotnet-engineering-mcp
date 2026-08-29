using System.Diagnostics;
using System.IO;
using System.Windows;

namespace EngineeringMcp.ControlCenter;

/// <summary>WPF test-app and ASP.NET fixture process management.</summary>
public partial class MainWindow
{
    private void LaunchFixture_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("Launch WPF fixture")) return;
        var process = EnsureFixtureRunning();
        if (process is not null)
            SetStatus("WPF fixture running.");
    }

    private void LaunchStack_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureDeveloperMode("Launch fixture stack")) return;
        var fixture = EnsureFixtureRunning();
        if (fixture is null) return;

        try
        {
            if (EnsureBackendRunning() is null) return;

            SetStatus("WPF + ASP.NET fixtures running.");
        }
        catch (Exception ex)
        {
            AppendLog("Launch ASP.NET fixture failed: " + ex.Message);
            SetStatus("Full stack launch failed.");
        }
    }

    private Process? EnsureBackendRunning(ProjectLayout? runtimeLayout = null)
    {
        var layout = runtimeLayout ?? _layout;
        if (_backendProcess is not null && !_backendProcess.HasExited)
            return _backendProcess;

        if (!File.Exists(layout.AspNetFixtureExecutable))
        {
            AppendLog("ASP.NET fixture executable is not built. Click Build solution or Run all dev tests first.");
            SetStatus("Build the solution before launching the full stack.");
            return null;
        }

        _backendProcess?.Dispose();
        _backendProcess = _runner.StartDetached(
            layout.AspNetFixtureExecutable,
            [],
            layout.Root,
            new Dictionary<string, string?> { ["ENGINEERING_MCP_BACKEND_TOKEN"] = _backendToken });
        AppendLog($"ASP.NET fixture started (PID {_backendProcess.Id}). Private adapter token remains in child environments only.");
        return _backendProcess;
    }

    private Process? EnsureFixtureRunning(ProjectLayout? runtimeLayout = null)
    {
        var layout = runtimeLayout ?? _layout;

        if (_fixtureProcess is not null && !_fixtureProcess.HasExited)
        {
            FixtureStatusText.Text = $"● Running · PID {_fixtureProcess.Id}";
            return _fixtureProcess;
        }

        try
        {
            if (!File.Exists(layout.FixtureExecutable))
            {
                AppendLog("WPF fixture executable is not built. Click Build solution or use Run WPF end-to-end.");
                SetStatus("Build the solution before launching the WPF fixture.");
                return null;
            }

            _fixtureProcess?.Dispose();
            _fixtureProcess = _runner.StartDetached(
                layout.FixtureExecutable,
                [],
                layout.Root,
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

    private static bool IsRunning(Process? process)
    {
        try { return process is not null && !process.HasExited; }
        catch { return false; }
    }
}
