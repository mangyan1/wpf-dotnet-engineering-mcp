using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.Security;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class DotNetTools
{
    [McpServerTool(Name = "dotnet_runtime_info", UseStructuredContent = true), Description("Returns bounded runtime/process diagnostics for an allowlisted process without dumping environment variables or credentials.")]
    public static ToolResult<RuntimeProcessInfo> RuntimeInfo([Description("Operating-system process identifier of an allowlisted target process.")] int processId, DotNetDiagnosticsService diagnostics, ToolAuthorization auth)
        => ToolRun.Sync(auth, ToolPolicies.Diagnose("dotnet_runtime_info"), processId.ToString(), () => diagnostics.GetRuntimeInfo(processId));

    [McpServerTool(Name = "dotnet_counters", UseStructuredContent = true), Description("Captures bounded System.Runtime EventCounters from an allowlisted .NET process. Only counter names/numeric values/units are returned.")]
    public static Task<ToolResult<IReadOnlyList<RuntimeCounterObservation>>> Counters(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        DotNetDiagnosticsService diagnostics,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("Bounded capture duration in milliseconds; the server applies a hard upper bound.")] int durationMs = 10_000)
        => ToolRun.Async(auth, ToolPolicies.Diagnose("dotnet_counters"), processId.ToString(),
            () => diagnostics.CaptureCountersAsync(processId, durationMs, cancellationToken));

    [McpServerTool(Name = "dotnet_gc_summary", UseStructuredContent = true), Description("Returns current GC/memory-related System.Runtime counter observations; this is telemetry, not a heap dump.")]
    public static Task<ToolResult<IReadOnlyList<RuntimeCounterObservation>>> GcSummary(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        DotNetDiagnosticsService diagnostics,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("Bounded capture duration in milliseconds; the server applies a hard upper bound.")] int durationMs = 10_000)
        => ToolRun.Async(auth, ToolPolicies.Diagnose("dotnet_gc_summary"), processId.ToString(), async () =>
        {
            var result = await diagnostics.CaptureCountersAsync(processId, durationMs, cancellationToken).ConfigureAwait(false);
            if (result.Success && result.Value is not null)
                result = ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Ok(result.Value.Where(x => x.Name.Contains("gc", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("heap", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("alloc", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("working-set", StringComparison.OrdinalIgnoreCase)).ToArray());
            return result;
        });

    [McpServerTool(Name = "dotnet_threads", UseStructuredContent = true), Description("Returns bounded OS process-thread metadata for an allowlisted target. It does not claim managed stack visibility.")]
    public static ToolResult<IReadOnlyList<ProcessThreadObservation>> Threads(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        DotNetDiagnosticsService diagnostics,
        ToolAuthorization auth,
        [Description("Maximum number of thread records to return; the server applies a hard upper bound.")] int maxThreads = 64)
        => ToolRun.Sync(auth, ToolPolicies.Diagnose("dotnet_threads"), processId.ToString(), () => diagnostics.GetThreads(processId, maxThreads));

    [McpServerTool(Name = "dotnet_modules", UseStructuredContent = true), Description("Returns bounded loaded module NAMES only for an allowlisted process; file paths are deliberately omitted.")]
    public static ToolResult<IReadOnlyList<ProcessModuleObservation>> Modules(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        DotNetDiagnosticsService diagnostics,
        ToolAuthorization auth,
        [Description("Maximum number of module names to return; the server applies a hard upper bound.")] int maxModules = 256)
        => ToolRun.Sync(auth, ToolPolicies.Diagnose("dotnet_modules"), processId.ToString(), () => diagnostics.GetModules(processId, maxModules));

    [McpServerTool(Name = "dotnet_exceptions", UseStructuredContent = true), Description("Observes .NET exception-start events for a bounded time window through EventPipe. Messages are redacted before MCP output.")]
    public static Task<ToolResult<IReadOnlyList<ExceptionObservation>>> Exceptions(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        DotNetDiagnosticsService diagnostics,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("Bounded observation duration in milliseconds; the server applies a hard upper bound.")] int durationMs = 10_000)
        => ToolRun.Async(auth, ToolPolicies.Diagnose("dotnet_exceptions"), processId.ToString(),
            () => diagnostics.CaptureExceptionsAsync(processId, durationMs, cancellationToken));

    [McpServerTool(Name = "dotnet_trace_start", UseStructuredContent = true), Description("Starts a bounded local EventPipe trace. Raw .nettrace bytes stay local and are never returned through MCP.")]
    public static ToolResult<TraceHandle> TraceStart([Description("Operating-system process identifier of an allowlisted target process.")] int processId, DotNetDiagnosticsService diagnostics, ToolAuthorization auth)
        => ToolRun.Sync(auth,
            ToolPolicyCatalog.Get("dotnet_trace_start").ToPolicy(),
            processId.ToString(), () => diagnostics.StartTrace(processId));

    [McpServerTool(Name = "dotnet_trace_stop", UseStructuredContent = true), Description("Stops a trace created by an earlier call. The returned handle never exposes the local trace path.")]
    public static Task<ToolResult<TraceHandle>> TraceStop(
        [Description("Opaque trace identifier previously returned by dotnet_trace_start.")] string traceId,
        DotNetDiagnosticsService diagnostics,
        ToolAuthorization auth,
        CancellationToken cancellationToken)
        => ToolRun.Async(auth, ToolPolicies.Diagnose("dotnet_trace_stop"), traceId, () => diagnostics.StopTraceAsync(traceId, cancellationToken));

    [McpServerTool(Name = "dotnet_capture_dump", UseStructuredContent = true), Description("PRIVILEGED: captures a heap dump for an allowlisted process into protected local storage. Only an opaque dumpId leaves the diagnostic boundary.")]
    public static ToolResult<object> CaptureDump([Description("Operating-system process identifier of an allowlisted target process.")] int processId, ClrMdService clrmd, ToolAuthorization auth)
        => ToolRun.Sync(auth, ToolPolicies.Privileged("dotnet_capture_dump"), processId.ToString(), () => clrmd.CaptureDump(processId));

    [McpServerTool(Name = "dotnet_analyze_dump", UseStructuredContent = true), Description("PRIVILEGED: analyzes a dump captured by an earlier call, by opaque dumpId; returns bounded stacks/types, not raw heap object values.")]
    public static ToolResult<DumpAnalysisSummary> AnalyzeDump(
        [Description("Opaque dump identifier previously returned by dotnet_capture_dump.")] string dumpId,
        ClrMdService clrmd,
        ToolAuthorization auth,
        [Description("Maximum number of threads analyzed; the server applies a hard upper bound.")] int maxThreads = 32,
        [Description("Maximum number of stack frames returned per thread; the server applies a hard upper bound.")] int maxFramesPerThread = 32)
        => ToolRun.Sync(auth, ToolPolicies.Privileged("dotnet_analyze_dump"), dumpId, () => clrmd.AnalyzeCapturedDump(dumpId, maxThreads, maxFramesPerThread));
}
