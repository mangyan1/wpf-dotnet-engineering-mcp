using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.NETCore.Client;

namespace EngineeringMcp.Diagnostics;

public sealed class ClrMdService(
    ProcessGuard processGuard,
    FileGuard fileGuard,
    FilePolicyProvider policyProvider,
    RedactionService redactor)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _capturedDumps = new(StringComparer.Ordinal);
    public ToolResult<object> CaptureDump(int processId, string? destinationDirectory = null)
    {
        if (!policyProvider.Current.AllowPrivilegedDiagnostics || policyProvider.Current.PermissionCeiling < PermissionLevel.SensitiveDiagnostics)
            return ToolResult<object>.Fail("PRIVILEGED_DIAGNOSTICS_DISABLED", "Dump capture requires SensitiveDiagnostics permission and explicit privileged-diagnostics policy.");

        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success)
            return ToolResult<object>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        allowed.Value?.Dispose();

        try
        {
            var directory = destinationDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotNetEngineeringMcp", "dumps");
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            PruneExpiredDumps(directory);
            var path = Path.Combine(directory, $"dump-{processId}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.dmp");
            new DiagnosticsClient(processId).WriteDump(DumpType.WithHeap, path, logDumpGeneration: false);
            var dumpId = Guid.NewGuid().ToString("N");
            _capturedDumps[dumpId] = path;
            return ToolResult<object>.Ok(new
            {
                captured = true,
                processId,
                dumpId,
                warning = "Dump remains local and is never returned through MCP. Use the opaque dumpId for analysis."
            });
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail("DUMP_CAPTURE_FAILED", redactor.Redact(ex.Message, policyProvider.Current.Pii));
        }
    }

    public ToolResult<DumpAnalysisSummary> AnalyzeCapturedDump(string dumpId, int maxThreads = 128, int maxFramesPerThread = 128)
    {
        if (!_capturedDumps.TryGetValue(dumpId, out var path) || !File.Exists(path))
            return ToolResult<DumpAnalysisSummary>.Fail("DUMP_REFERENCE_NOT_FOUND", "The local dump reference does not exist in this MCP session.");
        return AnalyzeTrustedLocalDump(path, maxThreads, maxFramesPerThread);
    }

    public ToolResult<DumpAnalysisSummary> AnalyzeDump(string dumpPath, int maxThreads = 128, int maxFramesPerThread = 128)
    {
        if (!policyProvider.Current.AllowPrivilegedDiagnostics || policyProvider.Current.PermissionCeiling < PermissionLevel.SensitiveDiagnostics)
            return ToolResult<DumpAnalysisSummary>.Fail("PRIVILEGED_DIAGNOSTICS_DISABLED", "Dump analysis requires SensitiveDiagnostics permission and explicit privileged-diagnostics policy.");

        var allowed = fileGuard.RequireReadable(dumpPath);
        if (!allowed.Success || allowed.Value is null)
            return ToolResult<DumpAnalysisSummary>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);

        return AnalyzeTrustedLocalDump(allowed.Value, maxThreads, maxFramesPerThread);
    }

    private ToolResult<DumpAnalysisSummary> AnalyzeTrustedLocalDump(string dumpPath, int maxThreads, int maxFramesPerThread)
    {
        if (!policyProvider.Current.AllowPrivilegedDiagnostics || policyProvider.Current.PermissionCeiling < PermissionLevel.SensitiveDiagnostics)
            return ToolResult<DumpAnalysisSummary>.Fail("PRIVILEGED_DIAGNOSTICS_DISABLED", "Dump analysis requires SensitiveDiagnostics permission and explicit privileged-diagnostics policy.");

        maxThreads = Math.Clamp(maxThreads, 1, 512);
        maxFramesPerThread = Math.Clamp(maxFramesPerThread, 1, 512);

        try
        {
            var options = new DataTargetOptions
            {
                // V1 is network-deny at runtime: do not contact symbol servers or interactive credential flows.
                SymbolPaths = Array.Empty<string>(),
                // Keep parsing bounds explicit. Network symbol resolution is disabled
                // above via an empty SymbolPaths list. DAC signature verification is
                // intentionally left at ClrMD's secure default (enabled on Windows).
                Limits = new DataTargetLimits
                {
                    MaxThreads = 5_000,
                    MaxModules = 20_000,
                    MaxStackFrames = 2_048
                }
            };
            using var target = DataTarget.LoadDump(dumpPath, options);
            var clrInfo = target.ClrVersions.FirstOrDefault();
            if (clrInfo is null)
                return ToolResult<DumpAnalysisSummary>.Fail("CLR_NOT_FOUND", "No CLR runtime was found in the dump.");
            using var runtime = clrInfo.CreateRuntime();
            var threads = new List<ThreadStackSummary>();
            foreach (var thread in runtime.Threads.Where(t => t.IsAlive).Take(maxThreads))
            {
                var frames = thread.EnumerateStackTrace().Take(maxFramesPerThread)
                    .Select(frame => SafeFrame(frame.ToString() ?? string.Empty))
                    .ToArray();
                threads.Add(new ThreadStackSummary(
                    thread.OSThreadId,
                    thread.IsAlive,
                    thread.CurrentException?.Type?.Name,
                    frames));
            }
            var aliveCount = runtime.Threads.Count(t => t.IsAlive);
            return ToolResult<DumpAnalysisSummary>.Ok(new DumpAnalysisSummary(
                "[LOCAL-REDACTED]",
                clrInfo.Version.ToString(),
                threads,
                aliveCount,
                aliveCount > maxThreads));
        }
        catch (Exception ex)
        {
            return ToolResult<DumpAnalysisSummary>.Fail("DUMP_ANALYSIS_FAILED", redactor.Redact(ex.Message, policyProvider.Current.Pii));
        }
    }

    private string SafeFrame(string frame)
    {
        var safe = redactor.Redact(frame, policyProvider.Current.Pii);
        return safe.Length > 2_048 ? safe[..2_048] + "…" : safe;
    }

    private static void PruneExpiredDumps(string directory)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var file in Directory.EnumerateFiles(directory, "dump-*.dmp", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        foreach (var pair in _capturedDumps.ToArray())
        {
            if (!_capturedDumps.TryRemove(pair.Key, out var path)) continue;
            try { File.Delete(path); } catch { }
        }
    }
}
