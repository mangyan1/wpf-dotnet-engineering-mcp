using System.Collections.Concurrent;

namespace EngineeringMcp.Host;

public sealed class ProcessOperationCoordinator : IDisposable
{
    // Hygiene bound, not a perf feature: one gate per distinct target process id ever seen would
    // otherwise grow for the host process lifetime. Once the map exceeds the ceiling, idle gates
    // (nobody pinned, semaphore free) are evicted opportunistically on insert. Evicted gates are
    // not disposed: no AvailableWaitHandle is ever created, so there is nothing to release.
    private const int MaxTrackedProcesses = 256;

    private readonly ConcurrentDictionary<int, Gate> _gates = new();

    public async ValueTask<IAsyncDisposable> EnterAsync(int processId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = _gates.GetOrAdd(processId, static _ => new Gate());
            // Pin by incrementing. Eviction claims an idle gate atomically before removing it, so a
            // pin result <= 0 means this gate was claimed for removal underneath us: unpin and
            // retry on a (possibly fresh) gate so two callers never gate one process with
            // different gates.
            if (Interlocked.Increment(ref gate.Users) <= 0)
            {
                Interlocked.Decrement(ref gate.Users);
                continue;
            }
            if (_gates.Count > MaxTrackedProcesses) EvictIdleGates();
            var releaser = await WaitPinnedAsync(gate, cancellationToken).ConfigureAwait(false);
            // Backstop for the claimed-gate race (a concurrent caller's increment can mask the
            // claim): if this gate left the map while we waited, a fresh replacement gate is
            // already serving the process, so release, unpin, and retry rather than run
            // concurrently with it.
            if (_gates.TryGetValue(processId, out var pinned) && ReferenceEquals(pinned, gate))
                return releaser;
            await releaser.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask<IAsyncDisposable> WaitPinnedAsync(Gate gate, CancellationToken cancellationToken)
    {
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref gate.Users);
            throw;
        }
        return new Releaser(gate);
    }

    private void EvictIdleGates()
    {
        // Claim an idle gate atomically (Users 0 -> Evicting) so no caller can pin it afterwards;
        // combined with the post-acquisition re-verify in EnterAsync, a gate is only ever removed
        // while no operation can still be waiting on it. Only the claimed entry is removed.
        foreach (var (processId, gate) in _gates)
        {
            if (_gates.Count <= MaxTrackedProcesses) return;
            if (Interlocked.CompareExchange(ref gate.Users, Evicting, 0) != 0) continue;
            if (!_gates.TryRemove(KeyValuePair.Create(processId, gate)))
                Interlocked.CompareExchange(ref gate.Users, 0, Evicting);
        }
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Semaphore.Dispose();
        _gates.Clear();
    }

    private const int Evicting = -1;

    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        // > 0: pinned by N callers. 0: idle, eviction may claim it. Evicting (-1): claimed for
        // removal; EnterAsync treats any pin result <= 0 as "lost the race" and retries.
        public int Users;
    }

    private sealed class Releaser(Gate gate) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                // Release before unpinning: the semaphore must be free while Users is still > 0 so
                // eviction cannot remove it underneath a subsequent acquirer.
                gate.Semaphore.Release();
                Interlocked.Decrement(ref gate.Users);
            }
            return ValueTask.CompletedTask;
        }
    }
}
