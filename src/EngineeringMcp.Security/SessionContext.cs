namespace EngineeringMcp.Security;

public sealed class SessionContext
{
    private readonly AsyncLocal<string?> _clientId = new();

    public string SessionId { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    public string ClientId => _clientId.Value ?? "stdio-local";

    public IDisposable BeginClientScope(string clientId)
    {
        var previous = _clientId.Value;
        _clientId.Value = string.IsNullOrWhiteSpace(clientId) ? "unknown-local" : clientId;
        return new Scope(() => _clientId.Value = previous);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
        }
    }
}
