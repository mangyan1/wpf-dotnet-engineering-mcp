using System.Diagnostics;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class WpfScreenshotIntegrationTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task Screenshot_ReturnsPngOnlyAfterTextAndSensitiveRegionsAreMasked()
    {
        var executable = FindFixtureExecutable();
        Assert.IsTrue(File.Exists(executable), "The WPF fixture build output is required.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = TestRepositoryLocator.FindRoot(),
            UseShellExecute = false
        };
        start.Environment["ENGINEERING_MCP_PROBE_TOKEN"] = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
        using var fixture = Process.Start(start) ?? throw new AssertFailedException("WPF fixture did not start.");
        try
        {
            await WaitForMainWindowAsync(fixture);
            var policy = McpPolicy.LockedDownDefault with
            {
                PermissionCeiling = PermissionLevel.UiRead,
                Processes = new ProcessPolicy([new AllowedProcessRule(Path.GetFileName(executable), executable)]),
                Screenshots = new ScreenshotPolicy(Enabled: true, MaskTextControls: true)
            };
            var provider = new FixedPolicyProvider(policy);
            var redactor = new RedactionService();
            var service = new WpfAutomationService(new ProcessGuard(provider), provider, redactor);
            try
            {
                var attached = service.Attach(fixture.Id);
                var screenshot = service.Screenshot(fixture.Id);

                Assert.IsTrue(attached.Success, attached.Error?.Message);
                Assert.IsTrue(screenshot.Success, screenshot.Error?.Message);
                Assert.IsGreaterThan(0, screenshot.Value?.RedactedRegions ?? 0);
                Assert.AreEqual("uia-text-and-sensitive-region-mask-v2", screenshot.Value?.RedactionMode);
                var png = Convert.FromBase64String(screenshot.Value!.Base64);
                CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            try
            {
                if (!fixture.HasExited)
                {
                    fixture.Kill(entireProcessTree: true);
                    await fixture.WaitForExitAsync();
                }
            }
            catch { }
        }
    }

    private static async Task WaitForMainWindowAsync(Process process)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited) throw new AssertFailedException("WPF fixture exited before opening its window.");
            if (process.MainWindowHandle != IntPtr.Zero) return;
            await Task.Delay(100);
        }
        throw new AssertFailedException("WPF fixture did not open a window within 15 seconds.");
    }

    private static string FindFixtureExecutable()
    {
        const string projectName = "EngineeringMcp.Wpf.TestApp";
        var artifacts = Environment.GetEnvironmentVariable(McpRuntimeDefaults.ArtifactsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(artifacts))
            return Path.Combine(Path.GetFullPath(artifacts), "bin", projectName, "debug", projectName + ".exe");
        return Path.Combine(TestRepositoryLocator.FindRoot(), "tests", projectName, "bin", "Debug",
            "net10.0-windows10.0.19041.0", projectName + ".exe");
    }

    private sealed class FixedPolicyProvider(McpPolicy policy) : FilePolicyProvider
    {
        public override McpPolicy Current { get; } = policy;
        public override string Source => "test";
    }
}
