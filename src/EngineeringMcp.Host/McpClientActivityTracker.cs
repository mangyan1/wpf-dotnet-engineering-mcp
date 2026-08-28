using EngineeringMcp.Contracts;

namespace EngineeringMcp.Host;

/// <summary>
/// Tracks only the presence of explicitly tagged local MCP clients. No request bodies,
/// tool names, tokens, or user data are retained. A recent completed request counts as
/// active briefly so stateless HTTP traffic remains visible between calls.
/// </summary>
internal sealed class McpClientActivityTracker
{
    private static readonly TimeSpan RecentActivityWindow = TimeSpan.FromSeconds(30);
    private int _activeVsCodeRequests;
    private long _lastVsCodeActivityUtcTicks;

    public IDisposable? BeginRequest(string? clientName)
    {
        if (!string.Equals(clientName, McpRuntimeDefaults.VsCodeClientName, StringComparison.OrdinalIgnoreCase))
            return null;

        MarkSeen();
        Interlocked.Increment(ref _activeVsCodeRequests);
        return new RequestScope(this);
    }

    public McpClientActivitySnapshot Snapshot()
    {
        var ticks = Volatile.Read(ref _lastVsCodeActivityUtcTicks);
        var lastSeen = ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : (DateTimeOffset?)null;
        var active = Volatile.Read(ref _activeVsCodeRequests) > 0 ||
                     lastSeen is not null && DateTimeOffset.UtcNow - lastSeen.Value <= RecentActivityWindow;
        return new McpClientActivitySnapshot(active, lastSeen);
    }

    private void EndRequest()
    {
        MarkSeen();
        Interlocked.Decrement(ref _activeVsCodeRequests);
    }

    private void MarkSeen() => Volatile.Write(ref _lastVsCodeActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

    private sealed class RequestScope(McpClientActivityTracker owner) : IDisposable
    {
        private McpClientActivityTracker? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndRequest();
    }
}

internal sealed record McpClientActivitySnapshot(bool VsCodeActive, DateTimeOffset? LastVsCodeActivityUtc);
