using System.IO.Pipes;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Redaction;

namespace EngineeringMcp.Wpf;

public interface IWpfProbeClient
{
    Task<ToolResult<ProbeResponse>> RequestAsync(int processId, ProbeRequest request, CancellationToken cancellationToken = default);
}

public sealed class WpfProbeClient(IProcessGuard processGuard, IRedactionService redactor, IPolicyProvider policyProvider) : IWpfProbeClient
{
    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public async Task<ToolResult<ProbeResponse>> RequestAsync(int processId, ProbeRequest request, CancellationToken cancellationToken = default)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success)
            return ToolResult<ProbeResponse>.Fail(allowed.Error!.Code, allowed.Error.Message);
        allowed.Value?.Dispose();

        var token = Environment.GetEnvironmentVariable("ENGINEERING_MCP_PROBE_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
            return ToolResult<ProbeResponse>.Fail("PROBE_TOKEN_UNAVAILABLE", "ENGINEERING_MCP_PROBE_TOKEN is not configured in the MCP host.");

        var pipeName = $"EngineeringMcp.WpfProbe.{processId}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // Named-pipe cancellation can be inconsistent once an I/O operation is in flight on Windows.
            // WaitAsync provides a hard upper bound for every stage, and disposing the pipe on exit aborts
            // any remaining native I/O rather than allowing an MCP tool call to hang for the OS pipe timeout.
            await pipe.ConnectAsync(timeout.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), timeout.Token)
                .ConfigureAwait(false);

            var authenticated = request with { Token = token };
            await BoundedJsonPipeProtocol.WriteAsync(pipe, authenticated, MaxRequestBytes, timeout.Token)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), timeout.Token)
                .ConfigureAwait(false);
            var response = await BoundedJsonPipeProtocol.ReadAsync<ProbeResponse>(pipe, MaxResponseBytes, timeout.Token)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), timeout.Token)
                .ConfigureAwait(false);
            return response is null
                ? ToolResult<ProbeResponse>.Fail("PROBE_INVALID_RESPONSE", "Probe response could not be parsed.")
                : ToolResult<ProbeResponse>.Ok(response);
        }
        catch (TimeoutException)
        {
            return ToolResult<ProbeResponse>.Fail("PROBE_TIMEOUT", "The authorized in-process WPF probe did not complete the pipe exchange within 5 seconds.", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult<ProbeResponse>.Fail("PROBE_TIMEOUT", "The authorized in-process WPF probe did not complete the pipe exchange within 5 seconds.", true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult<ProbeResponse>.Fail("PROBE_UNAVAILABLE", redactor.Redact(ex.Message, policyProvider.Current.Pii), true);
        }
    }
}
