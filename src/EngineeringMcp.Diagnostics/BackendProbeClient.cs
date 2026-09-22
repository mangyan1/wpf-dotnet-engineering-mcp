using System.IO.Pipes;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Diagnostics;

public sealed class BackendProbeClient(ProcessGuard processGuard, RedactionService redactor, FilePolicyProvider policyProvider) : IBackendProbeClient
{
    // Per-phase budgets mirror WpfProbeClient: the adapter server allows five seconds to read a
    // single request, so one short overall budget would spuriously fail healthy backends.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(12);

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
            await using var pipe = new NamedPipeClientStream(".", BoundedJsonPipeProtocol.AspNetProbePipeName(processId), PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TotalTimeout);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            connectTimeout.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            await BoundedJsonPipeProtocol.WriteAsync(pipe,
                new BackendProbeRequest(token, operation, Math.Clamp(limit, 1, 1_000), correlationId, afterSequence),
                BoundedJsonPipeProtocol.BackendProbeMaxRequestBytes, timeout.Token)
                .AsTask().WaitAsync(ExchangeTimeout, timeout.Token).ConfigureAwait(false);
            var response = await BoundedJsonPipeProtocol.ReadAsync<BackendProbeResponse>(pipe, BoundedJsonPipeProtocol.BackendProbeMaxResponseBytes, timeout.Token)
                .AsTask().WaitAsync(ExchangeTimeout, timeout.Token).ConfigureAwait(false);
            return response is null
                ? ToolResult<BackendProbeResponse>.Fail("BACKEND_INVALID_RESPONSE", "Backend probe response could not be parsed.")
                : ToolResult<BackendProbeResponse>.Ok(response);
        }
        catch (TimeoutException) { return FailTimeout(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return FailTimeout(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult<BackendProbeResponse>.Fail("BACKEND_PROBE_UNAVAILABLE", redactor.Redact(ex.Message, policyProvider.Current.Pii), true); }
    }

    private static ToolResult<BackendProbeResponse> FailTimeout()
        => ToolResult<BackendProbeResponse>.Fail("BACKEND_PROBE_TIMEOUT", "Timed out communicating with the approved backend diagnostic adapter.", true);
}
