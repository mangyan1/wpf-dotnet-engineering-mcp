using System.Collections.Concurrent;

namespace EngineeringMcp.Host;

public sealed class ProcessOperationCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

    public async ValueTask<IAsyncDisposable> EnterAsync(int processId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(processId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
