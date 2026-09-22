using System.Diagnostics;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using Microsoft.Extensions.Logging;

namespace EngineeringMcp.Host;

/// <summary>
/// The single shared tool execution boundary: authorize, run, audit the outcome, and convert
/// unexpected failures into structured MCP errors without leaking exception details.
/// Every tool in this host routes through ToolRun so the audit trail always records an outcome.
/// </summary>
internal static class ToolRun
{
    // Operational-trace logger, assigned from the host's registered logger at startup (Program.cs).
    // Null when the host runs without logging (e.g. unit tests); every use is null-safe. Lines carry
    // tool name, correlation id, outcome code, and duration only — no tool arguments, targets, or
    // error details, which belong to the audit trail.
    internal static ILogger? Logger;

    public static ToolResult<T> Sync<T>(ToolAuthorization auth, ToolPolicy policy, string? target, Func<ToolResult<T>> action)
    {
        var started = Stopwatch.StartNew();
        var allowed = auth.Authorize(policy, target);
        if (!allowed.Success)
        {
            Log(policy.ToolName, null, allowed.Error!.Code, started.ElapsedMilliseconds);
            return ToolResult<T>.From(allowed);
        }
        try
        {
            var result = action();
            var outcome = result.Success ? "OK" : result.Error?.Code ?? "FAILED";
            auth.Complete(allowed.Value!, policy, target, result.Success, outcome, started.ElapsedMilliseconds);
            Log(policy.ToolName, allowed.Value, outcome, started.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            return Failed<T>(auth, allowed.Value!, policy, target, started.ElapsedMilliseconds, ex);
        }
    }

    public static async Task<ToolResult<T>> Async<T>(ToolAuthorization auth, ToolPolicy policy, string? target, Func<Task<ToolResult<T>>> action, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var allowed = auth.Authorize(policy, target);
        if (!allowed.Success)
        {
            Log(policy.ToolName, null, allowed.Error!.Code, started.ElapsedMilliseconds);
            return ToolResult<T>.From(allowed);
        }
        try
        {
            // The MCP SDK binds tool-method CancellationToken parameters to the request token, which
            // is cancelled on client disconnect or notifications/cancelled. Observe it before and
            // during the action so a cancelled request is reported as CANCELLED even when the
            // underlying service ignores the token. WaitAsync with a non-cancelable token returns
            // the same task.
            cancellationToken.ThrowIfCancellationRequested();
            var result = await action().WaitAsync(cancellationToken).ConfigureAwait(false);
            var outcome = result.Success ? "OK" : result.Error?.Code ?? "FAILED";
            auth.Complete(allowed.Value!, policy, target, result.Success, outcome, started.ElapsedMilliseconds);
            Log(policy.ToolName, allowed.Value, outcome, started.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            return Failed<T>(auth, allowed.Value!, policy, target, started.ElapsedMilliseconds, ex);
        }
    }

    /// <summary>
    /// Shared inspect-then-mutate prologue for risk-classified mutations (wpf_click, diagnose_click):
    /// authorize a read under the public mutation tool name, inspect the target through the safe
    /// query path, classify it, and return the approved mutation policy. Every failure path (denied
    /// read, structured inspection failure, classifier failure, unexpected exception) is audited and
    /// traced exactly once, so the caller only forwards the returned failure.
    /// </summary>
    public static ToolResult<ToolPolicy> InspectBeforeMutation(
        ToolAuthorization auth, WpfAutomationService wpf, UiActionRiskClassifier classifier,
        string toolName, int processId, UiSelector selector)
    {
        var started = Stopwatch.StartNew();
        // The pre-mutation inspect is authorized under the public mutation tool name so that a
        // policy allowlisting that tool does not silently deny the whole operation.
        var readPolicy = new ToolPolicy(toolName, PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");
        var read = auth.Authorize(readPolicy, processId.ToString());
        if (!read.Success)
        {
            Log(toolName, null, read.Error!.Code, started.ElapsedMilliseconds);
            return ToolResult<ToolPolicy>.From(read);
        }
        try
        {
            var element = wpf.Query(processId, selector);
            if (!element.Success || element.Value is null)
            {
                var code = element.Error?.Code ?? "UNHANDLED_TOOL_ERROR";
                auth.Complete(read.Value!, readPolicy, processId.ToString(), false, code, started.ElapsedMilliseconds);
                Log(toolName, read.Value, code, started.ElapsedMilliseconds);
                return element.Success ? Unhandled<ToolPolicy>() : ToolResult<ToolPolicy>.From(element);
            }

            var risk = classifier.Classify(element.Value);
            if (!risk.Success)
            {
                var code = risk.Error!.Code;
                auth.Complete(read.Value!, readPolicy, processId.ToString(), false, code, started.ElapsedMilliseconds);
                Log(toolName, read.Value, code, started.ElapsedMilliseconds);
                return ToolResult<ToolPolicy>.From(risk);
            }

            return ToolResult<ToolPolicy>.Ok(ToolPolicyCatalog.Get(toolName).ToPolicy(risk.Value));
        }
        catch (Exception ex)
        {
            var (code, message, retryable) = Describe(ex);
            auth.Complete(read.Value!, readPolicy, processId.ToString(), false, code, started.ElapsedMilliseconds);
            Log(toolName, read.Value, code, started.ElapsedMilliseconds);
            return ToolResult<ToolPolicy>.Fail(code, message, retryable);
        }
    }

    // Structured failure for a service that violated its contract by returning Ok(null); guards
    // keep the invalid value from escaping the tool method as an exception.
    internal static ToolResult<T> Unhandled<T>()
        => ToolResult<T>.Fail("UNHANDLED_TOOL_ERROR", UnhandledFailureMessage, retryable: false);

    private const string UnhandledFailureMessage =
        "The tool failed unexpectedly. Raw exception details were withheld from the MCP boundary.";

    private static ToolResult<T> Failed<T>(ToolAuthorization auth, string correlationId, ToolPolicy policy, string? target, long elapsedMs, Exception ex)
    {
        var (code, message, retryable) = Describe(ex);
        auth.Complete(correlationId, policy, target, false, code, elapsedMs);
        Log(policy.ToolName, correlationId, code, elapsedMs);
        return ToolResult<T>.Fail(code, message, retryable);
    }

    private static (string Code, string Message, bool Retryable) Describe(Exception ex)
        => ex is OperationCanceledException
            ? ("CANCELLED", "The operation was cancelled before it completed.", true)
            : ("UNHANDLED_TOOL_ERROR", UnhandledFailureMessage, false);

    private static void Log(string toolName, string? correlationId, string outcome, long elapsedMs)
        => Logger?.LogInformation("Tool {ToolName} executed; correlation={CorrelationId} outcome={Outcome} elapsedMs={ElapsedMs}",
            toolName, correlationId ?? "-", outcome, elapsedMs);
}
