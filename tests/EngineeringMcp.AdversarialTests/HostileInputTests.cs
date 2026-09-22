using System.Diagnostics;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Host;
using EngineeringMcp.Security;
using EngineeringMcp.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.AdversarialTests;

/// <summary>
/// Adversarial coverage for the fail-closed surfaces a hostile caller, policy file, or probe
/// client hits: the policy engine's tool-list gates, the audit fail-closed latch, built-in
/// sensitive-file ceilings, reparse-point escape attempts, and bounded framed-JSON pipes.
/// </summary>
[TestClass]
public sealed class HostileInputTests
{
    // ---- Policy engine and tool publication ----

    [TestMethod]
    public void PolicyEngine_ToolLists_DenyUnknownAndDisabledTools()
    {
        var engine = new PolicyEngine();
        var policy = McpPolicy.LockedDownDefault with
        {
            PermissionCeiling = PermissionLevel.ApplicationDiagnostics,
            EnabledTools = ["system_health"],
            DisabledTools = ["wpf_click"]
        };
        var enabledButUnknown = new ToolPolicy("wpf_query", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");
        var explicitlyDisabled = new ToolPolicy("wpf_click", PermissionLevel.UiInteraction, RiskClass.StatefulMutation, "wpf.uia.interact");

        var notEnabled = engine.Authorize(enabledButUnknown, policy, capabilityAvailable: true);
        var disabled = engine.Authorize(explicitlyDisabled, policy, capabilityAvailable: true);

        Assert.AreEqual("TOOL_NOT_ENABLED", notEnabled.Code);
        StringAssert.Contains(notEnabled.Remediation, "enabledTools");
        Assert.AreEqual("TOOL_DISABLED", disabled.Code);
        StringAssert.Contains(disabled.Remediation, "disabledTools");
    }

    [TestMethod]
    public void PolicyEngine_DenyByDefault_RejectsDestructiveActionsWithoutExplicitApproval()
    {
        var engine = new PolicyEngine();
        var policy = McpPolicy.LockedDownDefault with { PermissionCeiling = PermissionLevel.UiInteraction };
        var decision = engine.Authorize(
            new ToolPolicy("wpf_type", PermissionLevel.UiInteraction, RiskClass.Destructive, "wpf.uia.interact"),
            policy,
            capabilityAvailable: true);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("EXPLICIT_APPROVAL_REQUIRED", decision.Code);
        StringAssert.Contains(decision.Remediation, "Do not bypass this gate");
        Assert.IsFalse(McpPolicy.LockedDownDefault.AllowDestructiveActions);
        Assert.IsFalse(McpPolicy.LockedDownDefault.AllowPrivilegedDiagnostics);
    }

    [TestMethod]
    public void ToolGate_AppliesConfiguredPolicyAndCapabilityState()
    {
        var policy = McpPolicy.LockedDownDefault with { PermissionCeiling = PermissionLevel.UiRead };
        var provider = new FixedPolicyProvider(policy);
        var gate = new ToolGate(new PolicyEngine(), provider);
        var toolPolicy = new ToolPolicy("wpf_query", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");

        Assert.IsTrue(gate.Authorize(toolPolicy, capabilityAvailable: true).Allowed);
        var unavailable = gate.Authorize(toolPolicy, capabilityAvailable: false);
        Assert.AreEqual("CAPABILITY_UNAVAILABLE", unavailable.Code);
    }

    [TestMethod]
    public void ToolPolicyCatalog_UnknownToolNameIsNotPublished()
    {
        var decision = ToolPolicyCatalog.Publication("not_an_engineering_tool", McpPolicy.LockedDownDefault);

        Assert.IsFalse(decision.Published);
        Assert.AreEqual("UNKNOWN_TOOL", decision.Code);
        StringAssert.Contains(decision.Remediation, "tools/list");
    }

    // ---- Audit fail-closed gate ----

    [TestMethod]
    public void ToolAuthorization_FailingAuditSink_DeniesThenLatchesUntilRestart()
    {
        var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with { PermissionCeiling = PermissionLevel.UiRead });
        var authorization = NewToolAuthorization(new ToolGate(new PolicyEngine(), provider), new ThrowingAuditSink(), provider);
        var toolPolicy = new ToolPolicy("wpf_query", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");

        var first = authorization.Authorize(toolPolicy);
        var second = authorization.Authorize(toolPolicy);

        Assert.AreEqual("AUDIT_UNAVAILABLE", first.Error?.Code);
        StringAssert.Contains(first.Error?.Remediation, "Do not disable audit");
        Assert.AreEqual("AUDIT_UNAVAILABLE", second.Error?.Code);
        StringAssert.Contains(second.Error?.Message, "audit trail is unhealthy");
    }

    [TestMethod]
    public void ToolAuthorization_CompletionAuditFailure_TripsTheGateForLaterCalls()
    {
        var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with { PermissionCeiling = PermissionLevel.UiRead });
        var sink = new ToggleableAuditSink();
        var authorization = NewToolAuthorization(new ToolGate(new PolicyEngine(), provider), sink, provider);
        var toolPolicy = new ToolPolicy("wpf_query", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");

        Assert.IsTrue(authorization.Authorize(toolPolicy).Success);
        sink.ThrowOnWrite = true;
        authorization.Complete("synthetic-correlation", toolPolicy, null, success: true, "OK");

        var after = authorization.Authorize(toolPolicy);
        Assert.AreEqual("AUDIT_UNAVAILABLE", after.Error?.Code);
    }

    [TestMethod]
    public void ToolAuthorization_ToolNotEnabled_IsDeniedAndAudited()
    {
        var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with
        {
            PermissionCeiling = PermissionLevel.UiRead,
            EnabledTools = ["system_health"]
        });
        var sink = new RecordingAuditSink();
        var authorization = NewToolAuthorization(new ToolGate(new PolicyEngine(), provider), sink, provider);
        var toolPolicy = new ToolPolicy("wpf_query", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");

        var denied = authorization.Authorize(toolPolicy, target: "target with bearer eyJhbGciOiJIUzI1NiJ9.a.b");

        Assert.AreEqual("TOOL_NOT_ENABLED", denied.Error?.Code);
        Assert.AreEqual(1, sink.Events.Count);
        Assert.AreEqual("DENY", sink.Events[0].Decision);
        Assert.AreEqual("TOOL_NOT_ENABLED", sink.Events[0].Result);
        Assert.IsFalse(sink.Events[0].Target?.Contains("eyJhbGci", StringComparison.Ordinal),
            "Audit targets must be redacted before persistence.");
    }

    [TestMethod]
    public void ToolAuthorization_AuditDisabledPolicy_DoesNotDenyOnBrokenSink()
    {
        var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with
        {
            PermissionCeiling = PermissionLevel.UiRead,
            Audit = new AuditPolicy(Enabled: false)
        });
        var authorization = NewToolAuthorization(new ToolGate(new PolicyEngine(), provider), new ThrowingAuditSink(), provider);
        var toolPolicy = new ToolPolicy("wpf_query", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");

        var allowed = authorization.Authorize(toolPolicy);

        Assert.IsTrue(allowed.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(allowed.Value));
    }

    // ---- FileGuard escape attempts ----

    [TestMethod]
    public void FileGuard_RelativeTraversal_CannotEscapeTheApprovedReadRoot()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mcp-adversarial-" + Guid.NewGuid().ToString("N"));
        var allowRoot = Path.Combine(temp, "allow");
        Directory.CreateDirectory(allowRoot);
        try
        {
            File.WriteAllText(Path.Combine(allowRoot, "View.xaml"), "<Grid />");
            File.WriteAllText(Path.Combine(temp, "outside.txt"), "secret");
            var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with
            {
                Filesystem = new FileSystemPolicy([allowRoot], Array.Empty<string>())
            });
            var guard = new FileGuard(provider);

            Assert.AreEqual("PATH_NOT_ALLOWED", guard.RequireReadable(Path.Combine(allowRoot, "..", "outside.txt")).Error?.Code);
            Assert.AreEqual("PATH_NOT_ALLOWED", guard.RequireReadable(temp).Error?.Code);
            Assert.IsTrue(guard.RequireReadable(Path.Combine(allowRoot, "sub", "..", "View.xaml")).Success);
        }
        finally { Directory.Delete(temp, true); }
    }

    [TestMethod]
    public void FileGuard_ReparsePointInsideApprovedRoot_IsDenied()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mcp-adversarial-" + Guid.NewGuid().ToString("N"));
        var allowRoot = Path.Combine(temp, "allow");
        Directory.CreateDirectory(allowRoot);
        var outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        var junction = Path.Combine(allowRoot, "link");
        try
        {
            // CreateJunction reports Inconclusive on filesystems that refuse
            // junctions; creating it inside the try keeps temp cleanup running.
            CreateJunction(junction, outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "synthetic");
            var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with
            {
                Filesystem = new FileSystemPolicy([allowRoot], Array.Empty<string>())
            });
            var guard = new FileGuard(provider);

            Assert.AreEqual("PATH_LINK_DENIED", guard.RequireReadable(junction).Error?.Code);
            Assert.AreEqual("PATH_LINK_DENIED", guard.RequireReadable(Path.Combine(junction, "secret.txt")).Error?.Code);
        }
        finally
        {
            try { Directory.Delete(junction, false); } catch { }
            Directory.Delete(temp, true);
        }
    }

    [TestMethod]
    public void FileGuard_BuiltInSensitiveRules_HoldEvenWithoutPolicyDenyGlobs()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mcp-adversarial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var name in new[] { ".env", "secrets.json", "server.pem", "backup.pfx", "core.dmp" })
                File.WriteAllText(Path.Combine(temp, name), "synthetic");
            File.WriteAllText(Path.Combine(temp, "Readme.md"), "synthetic");
            // Empty denyGlobs: the built-in SensitiveFileRules ceiling must hold on its own.
            var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with
            {
                Filesystem = new FileSystemPolicy([temp], Array.Empty<string>())
            });
            var guard = new FileGuard(provider);

            foreach (var name in new[] { ".env", "secrets.json", "server.pem", "backup.pfx", "core.dmp" })
                Assert.AreEqual("SENSITIVE_FILE_DENIED", guard.RequireReadable(Path.Combine(temp, name)).Error?.Code, name);
            Assert.IsTrue(guard.RequireReadable(Path.Combine(temp, "Readme.md")).Success);
        }
        finally { Directory.Delete(temp, true); }
    }

    // ---- Hostile framed-JSON pipe inputs ----

    [TestMethod]
    public async Task BoundedJsonPipeProtocol_RejectsNonPositiveAndIllFormedFrames()
    {
        await using var negative = new MemoryStream(BitConverter.GetBytes(-1));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            _ = await BoundedJsonPipeProtocol.ReadAsync<ToolFailure>(negative, 1024));

        await using var zero = new MemoryStream(BitConverter.GetBytes(0));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            _ = await BoundedJsonPipeProtocol.ReadAsync<ToolFailure>(zero, 1024));

        await using var illFormed = new MemoryStream();
        illFormed.Write(BitConverter.GetBytes(8));
        illFormed.Write("not json"u8);
        illFormed.Position = 0;
        await Assert.ThrowsExactlyAsync<JsonException>(async () =>
            _ = await BoundedJsonPipeProtocol.ReadAsync<ToolFailure>(illFormed, 1024));
    }

    [TestMethod]
    public async Task BoundedJsonPipeProtocol_WriteAsync_RefusesOversizedPayloadWithoutWriting()
    {
        await using var stream = new MemoryStream();
        var oversized = new ToolFailure("SYNTHETIC", new string('x', 2048));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await BoundedJsonPipeProtocol.WriteAsync(stream, oversized, 1024));

        Assert.AreEqual(0, stream.Length, "A rejected payload must not write even a length header to the pipe.");
    }

    [TestMethod]
    public void BoundedJsonPipeProtocol_FixedTimeEquals_RejectsWrongTokens()
    {
        Assert.IsTrue(BoundedJsonPipeProtocol.FixedTimeEquals("engineering-mcp-synthetic-token", "engineering-mcp-synthetic-token"));
        Assert.IsFalse(BoundedJsonPipeProtocol.FixedTimeEquals("engineering-mcp-synthetic-toksn", "engineering-mcp-synthetic-token"));
        Assert.IsFalse(BoundedJsonPipeProtocol.FixedTimeEquals("short", "engineering-mcp-synthetic-token"));
        Assert.IsFalse(BoundedJsonPipeProtocol.FixedTimeEquals("engineering-mcp-synthetic-token", ""));
    }

    // ---- Redaction of hostile secret shapes ----

    [TestMethod]
    public void Redactor_MasksAwsAccessKeysAndUrlEmbeddedCredentials()
    {
        var service = new RedactionService();
        var hostile = "key=AKIAIOSFODNN7EXAMPLE endpoint=https://svc_user:hunter2@internal.example/api";

        var result = service.Redact(hostile, PiiMode.Mask);

        Assert.IsFalse(result.Contains("AKIAIOSFODNN7EXAMPLE", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("[REDACTED:ACCESS_KEY]", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("hunter2", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("[REDACTED:CREDENTIAL]@", StringComparison.Ordinal));
    }

    private static ToolAuthorization NewToolAuthorization(ToolGate gate, IAuditSink sink, FixedPolicyProvider provider)
        => new(gate, new CapabilityRegistry(provider), sink, new SessionContext(), provider, new RedactionService());

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        process!.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive("This host could not create an NTFS junction for the reparse-point test.");
    }

    private sealed class ThrowingAuditSink : IAuditSink
    {
        public void Write(AuditEvent auditEvent) => throw new IOException("Synthetic audit destination failure.");
    }

    private sealed class ToggleableAuditSink : IAuditSink
    {
        public bool ThrowOnWrite { get; set; }

        public void Write(AuditEvent auditEvent)
        {
            if (ThrowOnWrite) throw new IOException("Synthetic audit destination failure.");
        }
    }

    private sealed class RecordingAuditSink : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public void Write(AuditEvent auditEvent) => Events.Add(auditEvent);
    }
}
