using EngineeringMcp.Contracts;
using EngineeringMcp.Redaction;
using EngineeringMcp.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.SecurityTests;

[TestClass]
public sealed class SecurityTests
{
    [TestMethod]
    public void Redactor_RemovesCredentialsAndMasksPii()
    {
        var service = new RedactionService();
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature password=SuperSecret123 email=john.smith@example.com";
        var result = service.Redact(input, PiiMode.Mask);
        Assert.IsFalse(result.Contains("eyJhbGci", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("SuperSecret123", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("john.smith@example.com", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ScreenshotClassification_DetectsPiiAndDefaultsOff()
    {
        var service = new RedactionService();

        Assert.IsTrue(service.LooksSensitiveOrPii("Customer email: test.person@example.invalid"));
        Assert.IsTrue(service.LooksSensitiveOrPii("Contact: 403-555-0199"));
        Assert.IsFalse(McpPolicy.LockedDownDefault.Screenshots.Enabled);
    }

    [TestMethod]
    public void PolicyEngine_DefaultDeny_RejectsUnavailableCapability()
    {
        var engine = new PolicyEngine();
        var policy = McpPolicy.LockedDownDefault;
        var decision = engine.Authorize(new ToolPolicy("wpf_click", PermissionLevel.UiInteraction, RiskClass.SafeMutation, "wpf.uia.interact"), policy, false);
        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("CAPABILITY_UNAVAILABLE", decision.Code);
    }

    [TestMethod]
    public void PolicyEngine_RejectsPrivilegedWithoutExplicitFlag()
    {
        var policy = McpPolicy.LockedDownDefault with { PermissionCeiling = PermissionLevel.SensitiveDiagnostics };
        var decision = new PolicyEngine().Authorize(new ToolPolicy("dotnet_capture_dump", PermissionLevel.SensitiveDiagnostics, RiskClass.Privileged, "dotnet.clrmd"), policy, true);
        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("PRIVILEGED_DIAGNOSTICS_DISABLED", decision.Code);
    }

    [TestMethod]
    public void FileGuard_BlocksOutsideRootAndDenyGlob()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mcp-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var allowedFile = Path.Combine(temp, "View.xaml"); File.WriteAllText(allowedFile, "<Grid />");
            var deniedFile = Path.Combine(temp, ".env"); File.WriteAllText(deniedFile, "TOKEN=fake");
            var policy = McpPolicy.LockedDownDefault with { Filesystem = new FileSystemPolicy([temp], ["**/.env"]) };
            var guard = new FileGuard(new FixedPolicyProvider(policy));
            Assert.IsTrue(guard.RequireReadable(allowedFile).Success);
            Assert.AreEqual("SENSITIVE_FILE_DENIED", guard.RequireReadable(deniedFile).Error?.Code);
            Assert.AreEqual("PATH_NOT_ALLOWED", guard.RequireReadable(Path.GetTempPath()).Error?.Code);
        }
        finally { Directory.Delete(temp, true); }
    }

    [TestMethod]
    public void Redactor_MasksVinPaymentCardAndLabeledIdentityData()
    {
        var service = new RedactionService();
        var input = "VIN 1M8GDM9AXKP042788; card 4111 1111 1111 1111; Customer Name: Synthetic Person; DOB: 2000-01-02";
        var result = service.Redact(input, PiiMode.Mask);

        Assert.IsFalse(result.Contains("1M8GDM9AXKP042788", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("4111 1111 1111 1111", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("Synthetic Person", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("2000-01-02", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PolicyValidator_RejectsPiiOffAndOpenNetwork()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => PolicyValidator.Validate(McpPolicy.LockedDownDefault with { Pii = PiiMode.Off }));
        Assert.ThrowsExactly<InvalidDataException>(() => PolicyValidator.Validate(McpPolicy.LockedDownDefault with
        {
            Network = new NetworkPolicy("allow", [])
        }));
    }

    [TestMethod]
    public async Task BoundedJsonPipeProtocol_RoundTripsAndRejectsOversizedFrame()
    {
        await using var stream = new MemoryStream();
        await BoundedJsonPipeProtocol.WriteAsync(stream, new ToolFailure("SYNTHETIC", "Synthetic failure"), 1024);
        stream.Position = 0;
        var value = await BoundedJsonPipeProtocol.ReadAsync<ToolFailure>(stream, 1024);
        Assert.AreEqual("SYNTHETIC", value?.Code);

        await using var oversized = new MemoryStream(BitConverter.GetBytes(2048));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await BoundedJsonPipeProtocol.ReadAsync<ToolFailure>(oversized, 1024));
    }

    private sealed class FixedPolicyProvider(McpPolicy policy) : IPolicyProvider
    {
        public McpPolicy Current { get; } = policy;
        public string Source => "test";
    }
}
