using System.Diagnostics;

namespace EngineeringMcp.ControlCenter;

/// <summary>
/// Kills this project's own stale executables left running after the Control Center
/// closed without stopping the shared MCP service. Name-based on purpose: these image
/// names are only produced by this repository's projects, and the current process is
/// always skipped.
/// </summary>
internal static class StaleProcessCleanup
{
    private static readonly string[] ImageNames =
    [
        "EngineeringMcp.Host.exe",
        "EngineeringMcp.AspNetCore.TestApp.exe",
        "EngineeringMcp.Wpf.TestApp.exe"
    ];

    public static int KillStale(Action<string> log)
    {
        var killed = 0;
        foreach (var image in ImageNames)
        {
            foreach (var process in Process.GetProcessesByName(image[..^4]))
            {
                if (process.Id == Environment.ProcessId)
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                    log($"Killed stale {image} (pid {process.Id}).");
                    killed++;
                }
                catch (Exception ex)
                {
                    log($"Could not stop stale {image} (pid {process.Id}): {ex.GetType().Name}.");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return killed;
    }
}