using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
        System.Windows.Window? occluder = null;
        Thread? occluderThread = null;
        try
        {
            await WaitForMainWindowAsync(fixture);
            using var occluderReady = new ManualResetEventSlim();
            occluderThread = new Thread(() =>
            {
                occluder = new System.Windows.Window
                {
                    Title = "Synthetic screenshot occluder",
                    Left = 0,
                    Top = 0,
                    Width = SystemParameters.PrimaryScreenWidth,
                    Height = SystemParameters.PrimaryScreenHeight,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    Topmost = true,
                    ShowInTaskbar = false,
                    Background = System.Windows.Media.Brushes.Magenta,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                occluder.Show();
                occluderReady.Set();
                Dispatcher.Run();
            });
            occluderThread.SetApartmentState(ApartmentState.STA);
            occluderThread.Start();
            Assert.IsTrue(occluderReady.Wait(TimeSpan.FromSeconds(5)), "Screenshot occluder did not open.");
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
                var guiAudit = new UiAuditService(service).GuiAudit(fixture.Id);

                Assert.IsTrue(attached.Success, attached.Error?.Message);
                Assert.IsTrue(screenshot.Success, screenshot.Error?.Message);
                Assert.IsTrue(guiAudit.Success, guiAudit.Error?.Message);
                foreach (var finding in guiAudit.Value!.Where(finding =>
                             finding.Category == "geometry" && finding.Evidence == "0x0"))
                {
                    var element = service.Find(fixture.Id, new UiSelector(Reference: finding.ElementReference));
                    Assert.IsTrue(element.Success, element.Error?.Message);
                    Assert.IsFalse(
                        element.Value!.ControlType == "Text" && string.IsNullOrWhiteSpace(element.Value.Name),
                        "Empty non-rendering TextBlocks must not be reported as visible geometry defects.");
                }
                Assert.IsGreaterThan(0, screenshot.Value?.RedactedRegions ?? 0);
                Assert.AreEqual("uia-text-and-sensitive-region-mask-v2", screenshot.Value?.RedactionMode);
                var png = Convert.FromBase64String(screenshot.Value!.Base64);
                CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
                using var stream = new MemoryStream(png);
                using var bitmap = new Bitmap(stream);
                Assert.IsFalse(ContainsMagentaOccluder(bitmap),
                    "The sanitized capture must render the target window independently of overlapping desktop windows.");
                Assert.IsFalse(ContainsLimeTextMarker(bitmap),
                    "The sanitized capture must cover rendered WPF text after native-frame coordinate translation.");
            }
            finally
            {
                service.Dispose();
            }
        }
        finally
        {
            if (occluder is not null)
                occluder.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            occluderThread?.Join(TimeSpan.FromSeconds(5));
            await StopProcessAsync(fixture);
        }
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch { }
        finally
        {
            process.Dispose();
        }
    }

    private static bool ContainsMagentaOccluder(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R >= 240 && pixel.G <= 20 && pixel.B >= 240)
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsLimeTextMarker(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R <= 30 && pixel.G >= 220 && pixel.B <= 30)
                    return true;
            }
        }

        return false;
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
