using EngineeringMcp.Security;
using EngineeringMcp.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EngineeringMcp.Host;

public sealed class ToolAuthorization(
    ToolGate gate,
    CapabilityRegistry capabilities,
    IAuditSink audit,
    SessionContext session,
    FilePolicyProvider policyProvider,
    RedactionService redaction)
{
    private int _auditHealthy = 1;
    private long _auditSequence;
    private readonly string _policyFingerprint = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(policyProvider.Current))))[..16];

    public ToolResult<string> Authorize(ToolPolicy policy, string? target = null)
    {
        if (policyProvider.Current.Audit.Enabled && Volatile.Read(ref _auditHealthy) == 0)
            return ToolResult<string>.Fail(
                "AUDIT_UNAVAILABLE",
                "Operation was denied because the required audit trail is unhealthy.",
                remediation: "Repair the configured audit destination, verify it is writable, and restart Engineering MCP. Do not disable audit to bypass this gate.");

        var correlation = Guid.NewGuid().ToString("N");
        var decision = gate.Authorize(policy, capabilities.IsAvailable(policy.CapabilityId));
        var sanitizedTarget = target is null ? null : redaction.Redact(target, policyProvider.Current.Pii);
        var auditWritten = TryWrite(new AuditEvent(DateTimeOffset.UtcNow, session.SessionId, policy.ToolName, sanitizedTarget,
            policy.RequiredPermission, policy.Risk, decision.Allowed ? "ALLOW" : "DENY",
            decision.Code, correlation, ClientId: session.ClientId, PolicyFingerprint: _policyFingerprint,
            Sequence: Interlocked.Increment(ref _auditSequence)));

        // Audit-enabled policy is a hard boundary: sensitive reads must not become invisible
        // merely because the audit destination is unavailable or full.
        if (!auditWritten && policyProvider.Current.Audit.Enabled)
        {
            Volatile.Write(ref _auditHealthy, 0);
            return ToolResult<string>.Fail(
                "AUDIT_UNAVAILABLE",
                "Operation was denied because the required audit record could not be persisted.",
                remediation: "Repair the configured audit destination, verify it is writable, and restart Engineering MCP. Do not disable audit to bypass this gate.");
        }

        return decision.Allowed
            ? ToolResult<string>.Ok(correlation)
            : ToolResult<string>.Fail(decision.Code, decision.Reason, remediation: decision.Remediation);
    }

    public void Complete(string correlationId, ToolPolicy policy, string? target, bool success, string resultCode, long durationMs = 0)
    {
        var sanitizedTarget = target is null ? null : redaction.Redact(target, policyProvider.Current.Pii);
        var written = TryWrite(new AuditEvent(DateTimeOffset.UtcNow, session.SessionId, policy.ToolName, sanitizedTarget,
            policy.RequiredPermission, policy.Risk, "EXECUTE", success ? resultCode : $"FAILED:{resultCode}", correlationId, durationMs,
            session.ClientId, _policyFingerprint, Interlocked.Increment(ref _auditSequence)));
        if (!written && policyProvider.Current.Audit.Enabled)
            Volatile.Write(ref _auditHealthy, 0);
    }

    private bool TryWrite(AuditEvent evt)
    {
        try
        {
            audit.WriteAsync(evt).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            // Do not leak audit backend details into MCP results. Authorize() decides whether failure must block execution.
            return false;
        }
    }
}
