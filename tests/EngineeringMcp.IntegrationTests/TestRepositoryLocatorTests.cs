using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed class TestRepositoryLocatorTests
{
    [TestMethod]
    public void FindRoot_UsesExplicitRepositoryOutsideArtifactTree()
    {
        var expected = TestRepositoryLocator.FindRoot();
        var unrelatedStart = Path.Combine(Path.GetTempPath(), "EngineeringMcp-Isolated-Test-Output");

        var actual = TestRepositoryLocator.FindRoot(expected, unrelatedStart);

        Assert.AreEqual(Path.GetFullPath(expected), actual);
    }

    [TestMethod]
    public void FindRoot_RejectsInvalidExplicitRepository()
    {
        var unrelatedRoot = Path.Combine(Path.GetTempPath(), "EngineeringMcp-Not-A-Repository");

        var exception = Assert.ThrowsExactly<DirectoryNotFoundException>(
            () => TestRepositoryLocator.FindRoot(unrelatedRoot, Environment.CurrentDirectory));

        StringAssert.Contains(exception.Message, "ENGINEERING_MCP_REPOSITORY_ROOT");
    }
}
