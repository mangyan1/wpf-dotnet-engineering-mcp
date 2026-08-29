using EngineeringMcp.Contracts;
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
    public void Redactor_PreservesDiagnosticDatesVersionsAndTargetFrameworkPaths()
    {
        var service = new RedactionService();
        const string input = "2026-08-29T14:32:11Z version 10.0.19041.0 bin/Debug/net10.0-windows10.0.19041.0";

        var result = service.Redact(input, PiiMode.Mask);

        Assert.AreEqual(input, result);
        Assert.IsFalse(service.LooksSensitiveOrPii(input));
    }

    [TestMethod]
    public void Redactor_StillMasksConventionalAndInternationalPhoneNumbers()
    {
        var service = new RedactionService();
        const string input = "Office 403-555-0199; international +1 (403) 555-0188";

        var result = service.Redact(input, PiiMode.Mask);

        Assert.DoesNotContain("403-555-0199", result, StringComparison.Ordinal);
        Assert.DoesNotContain("555-0188", result, StringComparison.Ordinal);
        Assert.IsTrue(service.LooksSensitiveOrPii(input));
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
        StringAssert.Contains(decision.Remediation, "allowPrivilegedDiagnostics");
    }

    [TestMethod]
    public void PolicyEngine_PermissionDenialNamesRequiredSettingAndControlCenterAction()
    {
        var decision = new PolicyEngine().Authorize(
            new ToolPolicy("dotnet_runtime_info", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "dotnet.eventpipe"),
            McpPolicy.LockedDownDefault,
            capabilityAvailable: true);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("PERMISSION_DENIED", decision.Code);
        StringAssert.Contains(decision.Reason, nameof(PermissionLevel.ApplicationDiagnostics));
        StringAssert.Contains(decision.Remediation, "permissionCeiling");
        StringAssert.Contains(decision.Remediation, "Control Center");
    }

    [TestMethod]
    public void ToolPolicyCatalog_WpfClickReportsInteractionPolicyAndProfile()
    {
        var definition = ToolPolicyCatalog.Get("wpf_click");
        var policy = McpPolicy.LockedDownDefault with
        {
            PermissionCeiling = PermissionLevel.ApplicationDiagnostics,
            EnabledToolProfiles = ["core", "wpf-read", "wpf-interact"]
        };

        Assert.AreEqual("wpf-interact", definition.Profile);
        Assert.AreEqual(PermissionLevel.UiInteraction, definition.RequiredPermission);
        Assert.AreEqual("wpf.uia.interact", definition.CapabilityId);
        Assert.IsTrue(definition.TargetRiskIsDynamic);
        Assert.IsTrue(ToolPolicyCatalog.Publication("wpf_click", policy).Published);
        Assert.IsTrue(new PolicyEngine().Authorize(definition.ToPolicy(), policy, capabilityAvailable: true).Allowed);
    }

    [TestMethod]
    public void ToolPolicyCatalog_ProfileDenialIsExplicitAndActionable()
    {
        var policy = McpPolicy.LockedDownDefault with
        {
            PermissionCeiling = PermissionLevel.ApplicationDiagnostics,
            EnabledToolProfiles = ["core", "wpf-read"]
        };

        var decision = ToolPolicyCatalog.Publication("wpf_click", policy);

        Assert.IsFalse(decision.Published);
        Assert.AreEqual("PROFILE_DISABLED", decision.Code);
        StringAssert.Contains(decision.Reason, "wpf-interact");
        StringAssert.Contains(decision.Remediation, "restart");
    }

    [TestMethod]
    public void PolicyDiagnostics_ExplainsLockedDownDefaultWithoutExposingPaths()
    {
        var report = PolicyDiagnostics.Analyze(McpPolicy.LockedDownDefault, "locked-down-default");

        Assert.AreEqual("locked-down-default", report.PolicySource);
        Assert.IsTrue(report.Findings.Any(finding => finding.Code == "POLICY_NOT_CONFIGURED"));
        Assert.IsTrue(report.Findings.Any(finding => finding.Code == "PROCESS_ALLOWLIST_EMPTY"));
        Assert.IsTrue(report.Findings.Any(finding => finding.Code == "SOURCE_ROOTS_EMPTY"));
        Assert.IsTrue(report.Findings.All(finding => !finding.Remediation.Contains("C:\\", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ProcessEnvironmentSanitizer_RemovesNetworkRelativeAndDuplicatePathEntries()
    {
        var first = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "engineering-mcp-path-a"));
        var second = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "engineering-mcp-path-b"));
        var input = string.Join(Path.PathSeparator,
            first,
            @"\\BuildServer\shared\WpfSampleWorkspace\.dotnet_cli\.dotnet\tools",
            "relative-tools",
            first,
            second);

        var sanitized = ProcessEnvironmentSanitizer.SanitizePath(input);
        var entries = sanitized.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        CollectionAssert.AreEqual(new[] { first, second }, entries);
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
        await BoundedJsonPipeProtocol.WriteAsync(stream,
            new ToolFailure("SYNTHETIC", "Synthetic failure", Remediation: "Use the synthetic safe action."),
            1024);
        stream.Position = 0;
        var value = await BoundedJsonPipeProtocol.ReadAsync<ToolFailure>(stream, 1024);
        Assert.AreEqual("SYNTHETIC", value?.Code);
        Assert.AreEqual("Use the synthetic safe action.", value?.Remediation);

        await using var oversized = new MemoryStream(BitConverter.GetBytes(2048));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await BoundedJsonPipeProtocol.ReadAsync<ToolFailure>(oversized, 1024));
    }

    private sealed class FixedPolicyProvider(McpPolicy policy) : FilePolicyProvider
    {
        public override McpPolicy Current { get; } = policy;
        public override string Source => "test";
    }
}
