using System.Net.Http;
using System.Text.Json;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.ControlCenter;

internal sealed record McpHealthSnapshot(int ProcessId, bool VsCodeActive, DateTimeOffset? LastVsCodeActivityUtc);

/// <summary>
/// Health client for the shared local MCP host. One instance per window with the
/// endpoint and bearer token injected at construction; the token rides per-request
/// headers, never HttpClient.DefaultRequestHeaders, so two Control Center windows
/// cannot clobber a shared static client's Authorization header.
/// </summary>
/// <param name="bearerToken">HTTP token shared with the host process environment.</param>
/// <param name="handler">Optional message-handler seam for tests.</param>
internal sealed class McpHealthClient(string? bearerToken, HttpMessageHandler? handler = null) : IDisposable
{
    // disposeHandler: false — an injected handler is owned by the caller (tests share
    // one handler across clients), so only the self-created default handler is ours.
    private readonly HttpClient _http = handler is null
        ? new HttpClient { Timeout = TimeSpan.FromSeconds(2) }
        : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<McpHealthSnapshot?> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, McpRuntimeDefaults.HealthEndpoint);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "ok", StringComparison.Ordinal) ||
                !root.TryGetProperty("server", out var server) || !string.Equals(server.GetString(), McpRuntimeDefaults.ServerName, StringComparison.Ordinal) ||
                !root.TryGetProperty("processId", out var processId) || !processId.TryGetInt32(out var pid))
                return null;

            var vsCodeActive = root.TryGetProperty("vsCodeActive", out var active) && active.ValueKind == JsonValueKind.True;
            DateTimeOffset? lastVsCodeActivityUtc = null;
            if (root.TryGetProperty("lastVsCodeActivityUtc", out var lastActivity) &&
                lastActivity.ValueKind == JsonValueKind.String &&
                lastActivity.TryGetDateTimeOffset(out var timestamp))
            {
                lastVsCodeActivityUtc = timestamp;
            }

            return new McpHealthSnapshot(pid, vsCodeActive, lastVsCodeActivityUtc);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}