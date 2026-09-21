using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.AdversarialTests;

// docs/ADR/0001 claims audit tampering "is detectable by re-walking RecordHash values" without
// any host-side secret; these tests hold that claim against the real sink, so a serializer-options
// drift or hash-input change fails here instead of silently breaking verifiability.
[TestClass]
public sealed class AuditChainTests
{
    private const string GenesisRecordHash = "0000000000000000000000000000000000000000000000000000000000000000";

    [TestMethod]
    public void RecordHashChain_IsVerifiableByRewalkingTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcp-adversarial-audit-{Guid.NewGuid():N}");
        try
        {
            using (var sink = new JsonLinesAuditSink(directory, retentionDays: 1))
            {
                sink.Write(new AuditEvent(DateTimeOffset.UtcNow, "session-1", "wpf_find", "42",
                    PermissionLevel.UiRead, RiskClass.Read, "ALLOW", "OK", "corr-1",
                    ClientId: "test", PolicyFingerprint: "test", Sequence: 1));
                sink.Write(new AuditEvent(DateTimeOffset.UtcNow, "session-1", "wpf_click", "42",
                    PermissionLevel.UiInteraction, RiskClass.StatefulMutation, "EXECUTE", "OK", "corr-1",
                    ClientId: "test", PolicyFingerprint: "test", Sequence: 2));
                sink.Write(new AuditEvent(DateTimeOffset.UtcNow, "session-1", "wpf_type", "42",
                    PermissionLevel.UiInteraction, RiskClass.SafeMutation, "EXECUTE", "FAILED:ELEMENT_NOT_FOUND", "corr-1",
                    ClientId: "test", PolicyFingerprint: "test", Sequence: 3));
            }

            var file = Directory.EnumerateFiles(directory, "audit-*.jsonl", SearchOption.TopDirectoryOnly).Single();
            var records = File.ReadLines(file)
                .Select(line => JsonSerializer.Deserialize<AuditEvent>(line))
                .ToArray();
            Assert.AreEqual(3, records.Length, "Every written record must persist as one JSON line.");

            var previous = GenesisRecordHash;
            foreach (var record in records)
            {
                Assert.IsNotNull(record);
                Assert.IsNotNull(record.RecordHash, "The sink must persist a per-record hash.");
                var canonical = JsonSerializer.Serialize(record with { RecordHash = null });
                var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous + canonical)));
                Assert.AreEqual(expected, record.RecordHash,
                    $"Record {record.Sequence} does not chain from its predecessor's stored hash.");
                previous = record.RecordHash;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void TamperedRecord_DivergesFromRecomputedChain()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcp-adversarial-audit-{Guid.NewGuid():N}");
        try
        {
            using (var sink = new JsonLinesAuditSink(directory, retentionDays: 1))
            {
                sink.Write(new AuditEvent(DateTimeOffset.UtcNow, "session-1", "wpf_find", "42",
                    PermissionLevel.UiRead, RiskClass.Read, "ALLOW", "OK", "corr-1",
                    ClientId: "test", PolicyFingerprint: "test", Sequence: 1));
                sink.Write(new AuditEvent(DateTimeOffset.UtcNow, "session-1", "wpf_click", "42",
                    PermissionLevel.UiInteraction, RiskClass.StatefulMutation, "EXECUTE", "OK", "corr-1",
                    ClientId: "test", PolicyFingerprint: "test", Sequence: 2));
            }

            var file = Directory.EnumerateFiles(directory, "audit-*.jsonl", SearchOption.TopDirectoryOnly).Single();
            var records = File.ReadLines(file)
                .Select(line => JsonSerializer.Deserialize<AuditEvent>(line))
                .ToArray();
            Assert.AreEqual(2, records.Length);
            var first = records[0]!;
            var second = records[1]!;

            // Re-walking the untouched record reproduces the stored hash...
            var untampered = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                first.RecordHash! + JsonSerializer.Serialize(second with { RecordHash = null }))));
            Assert.AreEqual(second.RecordHash, untampered, "Re-walk must reproduce an intact record.");

            // ...and a single flipped field no longer does, which is exactly what makes
            // tampering detectable: the verifier needs only the file and the genesis constant.
            var tampered = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                first.RecordHash! + JsonSerializer.Serialize(second with { Result = "FAILED:OK", RecordHash = null }))));
            Assert.AreNotEqual(second.RecordHash, tampered, "A modified record must break the chain.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}