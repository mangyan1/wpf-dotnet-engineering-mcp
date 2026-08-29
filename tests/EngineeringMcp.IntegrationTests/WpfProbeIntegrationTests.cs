using System.Diagnostics;
using System.Windows;
using EngineeringMcp.Contracts;
using EngineeringMcp.Probe.Wpf;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class WpfProbeIntegrationTests
{
    [TestMethod]
    public void Probe_StatusCompletesOverAuthenticatedPipeAndCanRestartCleanly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            const string token = "fedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafe";
            var previous = Environment.GetEnvironmentVariable("ENGINEERING_MCP_PROBE_TOKEN");
            Environment.SetEnvironmentVariable("ENGINEERING_MCP_PROBE_TOKEN", token);
            try
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using var process = Process.GetCurrentProcess();
                var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Test process path unavailable.");
                var policy = McpPolicy.LockedDownDefault with
                {
                    Processes = new ProcessPolicy([new AllowedProcessRule(process.ProcessName, path)])
                };
                var provider = new FixedPolicyProvider(policy);
                var redactor = new RedactionService();
                var client = new WpfProbeClient(new ProcessGuard(provider), redactor, provider);

                using (WpfProbe.Start())
                {
                    var first = client.RequestAsync(process.Id, new ProbeRequest(string.Empty, "status")).GetAwaiter().GetResult();
                    Assert.IsTrue(first.Success && first.Value?.Success == true, first.Error?.Message ?? first.Value?.ErrorMessage);
                }

                using (WpfProbe.Start())
                {
                    var restarted = client.RequestAsync(process.Id, new ProbeRequest(string.Empty, "status")).GetAwaiter().GetResult();
                    Assert.IsTrue(restarted.Success && restarted.Value?.Success == true, restarted.Error?.Message ?? restarted.Value?.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Environment.SetEnvironmentVariable("ENGINEERING_MCP_PROBE_TOKEN", previous);
                Application.Current?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "WPF probe integration test timed out.");
        if (failure is not null) throw failure;
    }

    private sealed class FixedPolicyProvider(McpPolicy policy) : FilePolicyProvider
    {
        public override McpPolicy Current { get; } = policy;
        public override string Source => "test";
    }
}
