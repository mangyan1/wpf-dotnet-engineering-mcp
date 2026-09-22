using System.Diagnostics;
using System.Text;
using EngineeringMcp.Security;

namespace EngineeringMcp.ControlCenter;

internal sealed class FixedProcessRunner
{
    public async Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }
        ProcessEnvironmentSanitizer.SanitizePathInPlace(startInfo.Environment);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onOutput(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {fileName}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    // Long-lived child launcher sharing RunAsync's output/error wiring. The caller owns
    // the returned Process lifetime plus any environment rules beyond the sanitizer.
    public Process StartMonitored(ProcessStartInfo startInfo, Action<string> onOutput, EventHandler? onExited = null, Action<Process>? onCreated = null)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        // Exited cannot fire before Start, so onCreated sees the process before any
        // exit event can race the caller's process-field assignment.
        onCreated?.Invoke(process);
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onOutput(e.Data); };
        if (onExited is not null) process.Exited += onExited;

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public Process StartDetached(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }
        ProcessEnvironmentSanitizer.SanitizePathInPlace(startInfo.Environment);

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
    }
}
