using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    long Sequence = 0,
    string? RecordHash = null);

/// <summary>
/// Receives one audit record per authorization or completion. Writing is synchronous because
/// every caller blocks on persistence before proceeding; audit-enabled policies fail closed
/// when a write throws, so the sink must surface failures synchronously.
/// </summary>
public interface IAuditSink
{
    /// <summary>Persists one audit record, throwing when the record could not be persisted.</summary>
    void Write(AuditEvent auditEvent);
}

public sealed class NullAuditSink : IAuditSink
{
    public void Write(AuditEvent auditEvent) { }
}

public sealed class JsonLinesAuditSink : IAuditSink, IDisposable
{
    // First record of every file chains from this constant instead of an unknown predecessor.
    private const string GenesisRecordHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private readonly int _retentionDays;
    private StreamWriter? _writer;
    private string _writerDay = string.Empty;
    private string _lastRecordHash = GenesisRecordHash;

    // Both serializations below must stay option-identical: the record hash is computed over the
    // canonical (hash-less) form and must be recomputable from the serialized file for verification.
    private static readonly JsonSerializerOptions RecordSerializerOptions = new();

    public JsonLinesAuditSink(string directory, int retentionDays = 30)
    {
        _directory = directory;
        _retentionDays = Math.Clamp(retentionDays, 1, 3650);
        Directory.CreateDirectory(directory);
        Prune();
        OpenWriter(DateTime.UtcNow);
    }

    public void Write(AuditEvent auditEvent)
    {
        _gate.Wait();
        try
        {
            var now = DateTime.UtcNow;
            var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (_writer is null || !string.Equals(_writerDay, day, StringComparison.Ordinal))
            {
                // Date rollover: rotate to the new day's file and re-run retention so a
                // long-lived host cannot keep writing into (and evading prune on) yesterday's file.
                DisposeWriter();
                Prune();
                OpenWriter(now);
            }

            var canonicalJson = JsonSerializer.Serialize(auditEvent with { RecordHash = null }, RecordSerializerOptions);
            var recordHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(_lastRecordHash + canonicalJson)));
            _lastRecordHash = recordHash;
            _writer!.WriteLine(JsonSerializer.Serialize(auditEvent with { RecordHash = recordHash }, RecordSerializerOptions));
        }
        finally { _gate.Release(); }
    }

    private void OpenWriter(DateTime now)
    {
        // One append stream per host process and UTC day prevents cross-process record
        // interleaving and file-lock contention. The start timestamp keeps a reused process id
        // from appending to a same-day file left by a prior process, which would corrupt
        // whole-file hash-chain verification.
        var path = Path.Combine(
            _directory,
            $"audit-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{now.ToString("HHmmss", CultureInfo.InvariantCulture)}-{Environment.ProcessId}.jsonl");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: false);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _writerDay = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        _lastRecordHash = GenesisRecordHash;
    }

    private void DisposeWriter()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "audit-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch { /* retention cleanup is best-effort; authorization writes still fail closed when required */ }
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { DisposeWriter(); }
        finally { _gate.Dispose(); }
    }
}
