using System.Diagnostics;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Host;

/// <summary>
/// The single shared tool execution boundary: authorize, run, audit the outcome, and convert
/// unexpected failures into structured MCP errors without leaking exception details.
/// Every tool in this host routes through ToolRun so the audit trail always records an outcome.
/// </summary>
internal static class ToolRun
{
    public static ToolResult<T> Sync<T>(ToolAuthorization auth, ToolPolicy policy, string? target, Func<ToolResult<T>> action)
    {
        var started = Stopwatch.StartNew();
        var allowed = auth.Authorize(policy, target);
        if (!allowed.Success) return ToolResult<T>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        try
        {
            var result = action();
            auth.Complete(allowed.Value!, policy, target, result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED", started.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            return Failed<T>(auth, allowed.Value!, policy, target, started.ElapsedMilliseconds, ex);
        }
    }

    public static async Task<ToolResult<T>> Async<T>(ToolAuthorization auth, ToolPolicy policy, string? target, Func<Task<ToolResult<T>>> action)
    {
        var started = Stopwatch.StartNew();
        var allowed = auth.Authorize(policy, target);
        if (!allowed.Success) return ToolResult<T>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        try
        {
            var result = await action().ConfigureAwait(false);
            auth.Complete(allowed.Value!, policy, target, result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED", started.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            return Failed<T>(auth, allowed.Value!, policy, target, started.ElapsedMilliseconds, ex);
        }
    }

    private static ToolResult<T> Failed<T>(ToolAuthorization auth, string correlationId, ToolPolicy policy, string? target, long elapsedMs, Exception ex)
    {
        var (code, message, retryable) = ex is OperationCanceledException
            ? ("CANCELLED", "The operation was cancelled before it completed.", true)
            : ("UNHANDLED_TOOL_ERROR", "The tool failed unexpectedly. Raw exception details were withheld from the MCP boundary.", false);
        auth.Complete(correlationId, policy, target, false, code, elapsedMs);
        return ToolResult<T>.Fail(code, message, retryable);
    }
}
