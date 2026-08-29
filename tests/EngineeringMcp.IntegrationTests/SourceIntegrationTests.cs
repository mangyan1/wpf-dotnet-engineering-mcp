using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Source;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed class SourceIntegrationTests
{
    [TestMethod]
    public void XamlAnalysis_FindsHardcodedColorAndAutomationId()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "MainWindow.xaml"), """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <StackPanel>
                    <Button AutomationProperties.AutomationId="SaveButton" Background="#202020" Content="Save" />
                    <TextBox Text="{Binding Path=MountPath}" />
                  </StackPanel>
                </Window>
                """);
            var policy = McpPolicy.LockedDownDefault with { Filesystem = new FileSystemPolicy([root], []) };
            var provider = new FixedPolicyProvider(policy);
            var service = new SourceIntelligenceService(new FileGuard(provider), provider, new RedactionService());
            var audit = service.AnalyzeXaml(root, 50);
            Assert.IsTrue(audit.Success);
            Assert.IsTrue(audit.Value!.Any(x => x.Rule == "WPF001_HARDCODED_COLOR"));
            Assert.IsTrue(service.FindAutomationId(root, "SaveButton").Value!.Count > 0);
            Assert.IsTrue(service.FindBinding(root, "MountPath").Value!.Count > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void XamlAnalysis_AcceptsOneApprovedFileWithoutScanningSiblings()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-source-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "Target.xaml");
            File.WriteAllText(target, """
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Button Background="#202020" Content="Target" />
                </Grid>
                """);
            File.WriteAllText(Path.Combine(root, "Unrelated.xaml"), """
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Button Background="#303030" Content="Unrelated" />
                </Grid>
                """);
            var policy = McpPolicy.LockedDownDefault with { Filesystem = new FileSystemPolicy([root], []) };
            var provider = new FixedPolicyProvider(policy);
            var service = new SourceIntelligenceService(new FileGuard(provider), provider, new RedactionService());

            var audit = service.AnalyzeXaml(target, 50);

            Assert.IsTrue(audit.Success, audit.Error?.Message);
            Assert.IsNotEmpty(audit.Value!);
            Assert.IsTrue(audit.Value!.All(finding => string.Equals(finding.File, target, StringComparison.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StackTraceMapping_StaysWithinRequestedSourceRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "mcp-source-map-" + Guid.NewGuid().ToString("N"));
        var requestedRoot = Path.Combine(parent, "requested");
        var siblingRoot = Path.Combine(parent, "sibling");
        Directory.CreateDirectory(requestedRoot);
        Directory.CreateDirectory(siblingRoot);

        try
        {
            var included = Path.Combine(requestedRoot, "Included.cs");
            var excluded = Path.Combine(siblingRoot, "Excluded.cs");
            File.WriteAllText(included, "class Included { }");
            File.WriteAllText(excluded, "class Excluded { }");

            var policy = McpPolicy.LockedDownDefault with { Filesystem = new FileSystemPolicy([parent], []) };
            var provider = new FixedPolicyProvider(policy);
            var service = new SourceIntelligenceService(new FileGuard(provider), provider, new RedactionService());
            var stackTrace = $"at Included.Run() in {included}:line 7{Environment.NewLine}at Excluded.Run() in {excluded}:line 9";

            var result = service.MapStackTrace(stackTrace, requestedRoot, 20);

            Assert.IsTrue(result.Success);
            Assert.HasCount(1, result.Value!);
            Assert.AreEqual(Path.GetFullPath(included), result.Value![0].File);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SemanticReferences_LoadApprovedSolutionAndResolveSymbolIdentity()
    {
        var root = TestRepositoryLocator.FindRoot();
        var policy = McpPolicy.LockedDownDefault with { Filesystem = new FileSystemPolicy([root], []) };
        var provider = new FixedPolicyProvider(policy);
        var service = new SourceIntelligenceService(new FileGuard(provider), provider, new RedactionService());

        var result = await service.FindSemanticReferencesAsync(root, "McpPolicy", 50);

        Assert.IsTrue(result.Success, result.Error?.Message);
        Assert.IsGreaterThan(0, result.Value?.Count ?? 0, "Expected at least one semantic reference to McpPolicy in the repository solution.");
        Assert.IsTrue(result.Value!.All(location => Path.GetFullPath(location.File).StartsWith(root, StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.Value!.All(location => location.Kind == "SemanticReference"));
    }

    private sealed class FixedPolicyProvider(McpPolicy policy) : FilePolicyProvider
    {
        public override McpPolicy Current { get; } = policy;
        public override string Source => "test";
    }
}
