using System.Text.Json;
using System.Text.Json.Serialization;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ApexDrivePolicyProvisionerTests
{
    [TestMethod]
    public void Provision_CreatesValidatedDurableLeastPrivilegePolicy()
    {
        var temp = CreateSyntheticRepository();
        try
        {
            var destination = Path.Combine(temp, "user-config", "policy.apexdrive.json");
            var result = ApexDrivePolicyProvisioner.Provision(temp, destination);

            Assert.AreEqual(Path.GetFullPath(temp), result.RepositoryRoot);
            Assert.AreEqual(Path.GetFullPath(destination), result.PolicyPath);
            Assert.IsTrue(File.Exists(result.PolicyPath));

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };
            var policy = JsonSerializer.Deserialize<McpPolicy>(File.ReadAllText(result.PolicyPath), options);

            Assert.IsNotNull(policy);
            PolicyValidator.Validate(policy);
            Assert.AreEqual(PermissionLevel.ApplicationDiagnostics, policy.PermissionCeiling);
            Assert.AreEqual("deny", policy.Network.Default);
            Assert.AreEqual(PiiMode.Mask, policy.Pii);
            Assert.IsTrue(policy.Screenshots.Enabled);
            Assert.IsTrue(policy.Screenshots.MaskTextControls);
            Assert.IsFalse(policy.AllowDestructiveActions);
            Assert.IsFalse(policy.AllowPrivilegedDiagnostics);
            Assert.HasCount(3, policy.Processes.Allow);
            var workstationRule = policy.Processes.Allow.Single(rule => rule.Name == "ApexDrive.Workstation.Shell.exe");
            Assert.AreEqual(result.WorkstationExecutable, workstationRule.Path);
            Assert.IsTrue(policy.Processes.Allow.Any(rule => rule.Name == "ApexDrive.CustomerServer.Host.exe"));
            Assert.IsTrue(policy.Processes.Allow.Any(rule => rule.Name == "PageSmoke.exe"));
            Assert.HasCount(1, policy.Filesystem.ReadRoots);
            Assert.AreEqual(Path.GetFullPath(temp), policy.Filesystem.ReadRoots[0]);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void CreatePolicy_RejectsDirectoryWithoutApexDriveSentinels()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EngineeringMcp.PolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => ApexDrivePolicyProvisioner.CreatePolicy(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string CreateSyntheticRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "EngineeringMcp.PolicyTests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(
            root,
            "src",
            "WorkstationClient",
            "ApexDrive.Workstation.Shell",
            "ApexDrive.Workstation.Shell.csproj");
        var executable = Path.Combine(
            root,
            "src",
            "WorkstationClient",
            "ApexDrive.Workstation.Shell",
            "bin",
            "Debug",
            "net10.0-windows10.0.19041.0",
            "ApexDrive.Workstation.Shell.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(Path.Combine(root, "ApexDrivePlatform.sln"), string.Empty);
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(executable, string.Empty);
        return root;
    }
}
