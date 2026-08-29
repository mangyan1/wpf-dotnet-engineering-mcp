using System.IO.Pipes;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Diagnostics;

public sealed class BackendProbeClient(ProcessGuard processGuard, RedactionService redactor, FilePolicyProvider policyProvider)
{
    private const int MaxRequestBytes = 32 * 1024;
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    public async Task<ToolResult<BackendProbeResponse>> RequestAsync(
        int processId,
        string operation,
        int limit = 100,
        CancellationToken cancellationToken = default,
        string? correlationId = null,
        long? afterSequence = null)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success)
            return ToolResult<BackendProbeResponse>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        allowed.Value?.Dispose();

        var token = Environment.GetEnvironmentVariable("ENGINEERING_MCP_BACKEND_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
            return ToolResult<BackendProbeResponse>.Fail("BACKEND_TOKEN_UNAVAILABLE", "ENGINEERING_MCP_BACKEND_TOKEN is not configured.");

        try
        {
            await using var pipe = new NamedPipeClientStream(".", $"EngineeringMcp.AspNetProbe.{processId}", PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await BoundedJsonPipeProtocol.WriteAsync(pipe,
                new BackendProbeRequest(token, operation, Math.Clamp(limit, 1, 1_000), correlationId, afterSequence),
                MaxRequestBytes, timeout.Token).ConfigureAwait(false);
            var response = await BoundedJsonPipeProtocol.ReadAsync<BackendProbeResponse>(pipe, MaxResponseBytes, timeout.Token).ConfigureAwait(false);
            return response is null
                ? ToolResult<BackendProbeResponse>.Fail("BACKEND_INVALID_RESPONSE", "Backend probe response could not be parsed.")
                : ToolResult<BackendProbeResponse>.Ok(response);
        }
        catch (OperationCanceledException) { return ToolResult<BackendProbeResponse>.Fail("BACKEND_PROBE_TIMEOUT", "Timed out connecting to the approved backend diagnostic adapter.", true); }
        catch (Exception ex) { return ToolResult<BackendProbeResponse>.Fail("BACKEND_PROBE_UNAVAILABLE", redactor.Redact(ex.Message, policyProvider.Current.Pii), true); }
    }
}
