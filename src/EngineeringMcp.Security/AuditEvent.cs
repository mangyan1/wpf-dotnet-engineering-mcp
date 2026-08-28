using System.Text.Json;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed record AuditEvent(
    DateTimeOffset TimestampUtc,
    string SessionId,
    string Tool,
    string? Target,
    PermissionLevel Permission,
    RiskClass Risk,
    string Decision,
    string Result,
    string CorrelationId,
    long DurationMs = 0,
    string ClientId = "unknown-local",
    string PolicyFingerprint = "unknown",
    long Sequence = 0);

public interface IAuditSink
{
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

public sealed class NullAuditSink : IAuditSink
{
    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public sealed class JsonLinesAuditSink : IAuditSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StreamWriter _writer;

    public JsonLinesAuditSink(string directory, int retentionDays = 30)
    {
        Directory.CreateDirectory(directory);
        Prune(directory, Math.Clamp(retentionDays, 1, 3650));
        // One append stream per host process prevents cross-process record interleaving and file-lock contention.
        var path = Path.Combine(directory, $"audit-{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}.jsonl");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public async ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(auditEvent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static void Prune(string directory, int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(directory, "audit-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch { /* retention cleanup is best-effort; authorization writes still fail closed when required */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
