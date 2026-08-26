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
    public static ToolResult<RuntimeProcessInfo> RuntimeInfo(int processId, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth)
        => Run("dotnet_runtime_info", processId, ToolPolicies.Diagnose("dotnet_runtime_info"), () => diagnostics.GetRuntimeInfo(processId), auth);


    [McpServerTool(Name = "dotnet_counters", UseStructuredContent = true), Description("Captures bounded System.Runtime EventCounters from an allowlisted .NET process. Only counter names/numeric values/units are returned.")]
    public static async Task<ToolResult<IReadOnlyList<RuntimeCounterObservation>>> Counters(int processId, int durationMs, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth, CancellationToken cancellationToken)
    {
        var policy = ToolPolicies.Diagnose("dotnet_counters");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await diagnostics.CaptureCountersAsync(processId, durationMs, cancellationToken).ConfigureAwait(false);
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }

    [McpServerTool(Name = "dotnet_gc_summary", UseStructuredContent = true), Description("Returns current GC/memory-related System.Runtime counter observations; this is telemetry, not a heap dump.")]
    public static async Task<ToolResult<IReadOnlyList<RuntimeCounterObservation>>> GcSummary(int processId, int durationMs, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth, CancellationToken cancellationToken)
    {
        var policy = ToolPolicies.Diagnose("dotnet_gc_summary");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await diagnostics.CaptureCountersAsync(processId, durationMs, cancellationToken).ConfigureAwait(false);
        if (result.Success && result.Value is not null)
            result = ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Ok(result.Value.Where(x => x.Name.Contains("gc", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("heap", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("alloc", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("working-set", StringComparison.OrdinalIgnoreCase)).ToArray());
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }

    [McpServerTool(Name = "dotnet_threads", UseStructuredContent = true), Description("Returns bounded OS process-thread metadata for an allowlisted target. It does not claim managed stack visibility.")]
    public static ToolResult<IReadOnlyList<ProcessThreadObservation>> Threads(int processId, int maxThreads, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth)
        => Run("dotnet_threads", processId, ToolPolicies.Diagnose("dotnet_threads"), () => diagnostics.GetThreads(processId, maxThreads), auth);

    [McpServerTool(Name = "dotnet_modules", UseStructuredContent = true), Description("Returns bounded loaded module NAMES only for an allowlisted process; file paths are deliberately omitted.")]
    public static ToolResult<IReadOnlyList<ProcessModuleObservation>> Modules(int processId, int maxModules, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth)
        => Run("dotnet_modules", processId, ToolPolicies.Diagnose("dotnet_modules"), () => diagnostics.GetModules(processId, maxModules), auth);

    [McpServerTool(Name = "dotnet_exceptions", UseStructuredContent = true), Description("Observes .NET exception-start events for a bounded time window through EventPipe. Messages are redacted before MCP output.")]
    public static async Task<ToolResult<IReadOnlyList<ExceptionObservation>>> Exceptions(int processId, int durationMs, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth, CancellationToken cancellationToken)
    {
        var policy = ToolPolicies.Diagnose("dotnet_exceptions");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<IReadOnlyList<ExceptionObservation>>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await diagnostics.CaptureExceptionsAsync(processId, durationMs, cancellationToken).ConfigureAwait(false);
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }

    [McpServerTool(Name = "dotnet_trace_start", UseStructuredContent = true), Description("Starts a bounded local EventPipe trace. Raw .nettrace bytes stay local and are never returned through MCP.")]
    public static ToolResult<TraceHandle> TraceStart(int processId, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth)
        => Run("dotnet_trace_start", processId,
            new ToolPolicy("dotnet_trace_start", PermissionLevel.ApplicationDiagnostics, RiskClass.StatefulMutation, "dotnet.eventpipe"),
            () => diagnostics.StartTrace(processId), auth);

    [McpServerTool(Name = "dotnet_trace_stop", UseStructuredContent = true), Description("Stops a trace created by this MCP session. The returned handle never exposes the local trace path.")]
    public static async Task<ToolResult<TraceHandle>> TraceStop(string traceId, IDotNetDiagnosticsService diagnostics, IToolAuthorization auth, CancellationToken cancellationToken)
    {
        var policy = ToolPolicies.Diagnose("dotnet_trace_stop");
        var allowed = auth.Authorize(policy, traceId);
        if (!allowed.Success) return ToolResult<TraceHandle>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await diagnostics.StopTraceAsync(traceId, cancellationToken).ConfigureAwait(false);
        auth.Complete(allowed.Value!, policy, traceId, result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }

    [McpServerTool(Name = "dotnet_capture_dump", UseStructuredContent = true), Description("PRIVILEGED: captures a heap dump for an allowlisted process into protected local storage. Only an opaque dumpId leaves the diagnostic boundary.")]
    public static ToolResult<object> CaptureDump(int processId, IClrMdService clrmd, IToolAuthorization auth)
        => Run("dotnet_capture_dump", processId, ToolPolicies.Privileged("dotnet_capture_dump"), () => clrmd.CaptureDump(processId), auth);

    [McpServerTool(Name = "dotnet_analyze_dump", UseStructuredContent = true), Description("PRIVILEGED: analyzes a dump captured in this MCP session by opaque dumpId; returns bounded stacks/types, not raw heap object values.")]
    public static ToolResult<DumpAnalysisSummary> AnalyzeDump(string dumpId, int maxThreads, int maxFramesPerThread, IClrMdService clrmd, IToolAuthorization auth)
    {
        var policy = ToolPolicies.Privileged("dotnet_analyze_dump");
        var allowed = auth.Authorize(policy, dumpId);
        if (!allowed.Success) return ToolResult<DumpAnalysisSummary>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = clrmd.AnalyzeCapturedDump(dumpId, maxThreads, maxFramesPerThread);
        auth.Complete(allowed.Value!, policy, dumpId, result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }

    private static ToolResult<T> Run<T>(string tool, int processId, ToolPolicy policy, Func<ToolResult<T>> action, IToolAuthorization auth)
    {
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<T>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = action();
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }
}
