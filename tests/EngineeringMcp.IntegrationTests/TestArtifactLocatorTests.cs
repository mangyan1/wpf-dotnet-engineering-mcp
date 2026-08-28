using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed class TestArtifactLocatorTests
{
    [TestMethod]
    public void FindExecutable_UsesIsolatedArtifactLayoutWhenConfigured()
    {
        var artifacts = Path.Combine(Path.GetTempPath(), "EngineeringMcp-Isolated-Artifacts");

        var actual = TestArtifactLocator.FindHostExecutable(
            "C:\\unused-repository",
            artifacts,
            "Debug",
            "net10.0-windows10.0.19041.0");

        var expected = Path.Combine(
            Path.GetFullPath(artifacts),
            "bin",
            "EngineeringMcp.Host",
            "debug",
            "EngineeringMcp.Host.exe");
        Assert.AreEqual(expected, actual);
    }
}
