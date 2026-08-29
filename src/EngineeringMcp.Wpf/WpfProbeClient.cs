using System.IO.Pipes;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Wpf;

public sealed class WpfProbeClient(ProcessGuard processGuard, RedactionService redactor, FilePolicyProvider policyProvider)
{
    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public async Task<ToolResult<ProbeResponse>> RequestAsync(int processId, ProbeRequest request, CancellationToken cancellationToken = default)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success)
            return ToolResult<ProbeResponse>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        allowed.Value?.Dispose();

        var token = Environment.GetEnvironmentVariable("ENGINEERING_MCP_PROBE_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
            return ToolResult<ProbeResponse>.Fail("PROBE_TOKEN_UNAVAILABLE", "ENGINEERING_MCP_PROBE_TOKEN is not configured in the MCP host.");

        var pipeName = $"EngineeringMcp.WpfProbe.{processId}";
        using var overallTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            NamedPipeClientStream? pipe = null;
            for (var attempt = 1; attempt <= 3 && pipe is null; attempt++)
            {
                var candidate = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await candidate.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                    pipe = candidate;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 3)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(100 * attempt, overallTimeout.Token).ConfigureAwait(false);
                }
                catch (IOException) when (attempt < 3)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(100 * attempt, overallTimeout.Token).ConfigureAwait(false);
                }
            }

            if (pipe is null)
                return ToolResult<ProbeResponse>.Fail("PROBE_NOT_INSTALLED", "No authenticated WPF probe pipe was found for the target process. The target must explicitly start EngineeringMcp.Probe.Wpf.", true);

            await using (pipe)
            {
                var authenticated = request with { Token = token };
                await BoundedJsonPipeProtocol.WriteAsync(pipe, authenticated, MaxRequestBytes, overallTimeout.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(8), overallTimeout.Token)
                    .ConfigureAwait(false);
                var response = await BoundedJsonPipeProtocol.ReadAsync<ProbeResponse>(pipe, MaxResponseBytes, overallTimeout.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(8), overallTimeout.Token)
                    .ConfigureAwait(false);
                return response is null
                    ? ToolResult<ProbeResponse>.Fail("PROBE_INVALID_RESPONSE", "Probe response could not be parsed.")
                    : ToolResult<ProbeResponse>.Ok(response);
            }
        }
        catch (TimeoutException)
        {
            return ToolResult<ProbeResponse>.Fail("PROBE_TIMEOUT", "The authorized in-process WPF probe did not complete the pipe exchange within 12 seconds. Check whether the target installed the probe and whether its dispatcher is responsive.", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult<ProbeResponse>.Fail("PROBE_NOT_INSTALLED", "No authenticated WPF probe pipe became available for the target process. The target must explicitly start EngineeringMcp.Probe.Wpf.", true);
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
