using System.Text.Json;
using System.Text.Json.Serialization;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class WpfWorkspacePolicyProvisionerTests
{
    [TestMethod]
    public void Provision_DiscoversMultipleWpfApplicationsAndCreatesValidatedPolicy()
    {
        var temp = CreateSyntheticWorkspace();
        try
        {
            var destination = Path.Combine(temp, "user-config", "policy.workspace.json");
            var result = WpfWorkspacePolicyProvisioner.Provision(temp, destination);

            Assert.AreEqual(Path.GetFullPath(temp), result.WorkspaceRoot);
            Assert.AreEqual(Path.GetFullPath(destination), result.PolicyPath);
            Assert.IsTrue(File.Exists(result.PolicyPath));
            Assert.HasCount(2, result.Applications);
            Assert.IsTrue(result.Applications.Any(application => application.Name == "Sample.Desktop.exe"));
            Assert.IsTrue(result.Applications.Any(application => application.Name == "Workshop.Client.exe"));

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
            Assert.HasCount(2, policy.Processes.Allow);
            Assert.IsTrue(policy.Processes.Allow.All(rule => rule.Path is not null && Path.IsPathFullyQualified(rule.Path)));
            Assert.IsTrue(policy.Processes.Allow.All(rule => rule.Path!.StartsWith(result.WorkspaceRoot, StringComparison.OrdinalIgnoreCase)));
            Assert.HasCount(1, policy.Filesystem.ReadRoots);
            Assert.AreEqual(Path.GetFullPath(temp), policy.Filesystem.ReadRoots[0]);
            Assert.IsTrue(policy.Filesystem.DenyGlobs.Contains("**/.env.*"));
            Assert.IsTrue(policy.Filesystem.DenyGlobs.Contains("**/*.sqlite"));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void DiscoverApplications_IgnoresNonWpfAndLibraryProjects()
    {
        var temp = CreateSyntheticWorkspace();
        try
        {
            var applications = WpfWorkspacePolicyProvisioner.DiscoverApplications(temp);

            Assert.HasCount(2, applications);
            Assert.IsFalse(applications.Any(application => application.Name == "Console.Utility.exe"));
            Assert.IsFalse(applications.Any(application => application.Name == "Visual.Components.exe"));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void DiscoverApplications_RequiresBuiltWpfExecutable()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            CreateProject(temp, "src", "Unbuilt.Client", "Unbuilt.Client", useWpf: true, outputType: "WinExe", createExecutable: false);

            var exception = Assert.ThrowsExactly<WpfWorkspaceDiscoveryException>(
                () => WpfWorkspacePolicyProvisioner.DiscoverApplications(temp));

            StringAssert.Contains(exception.Message, "select its executable", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void DiscoverApplications_SupportsClassicWpfProjectMetadata()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var projectDirectory = Path.Combine(temp, "Legacy.Desktop");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(
                Path.Combine(projectDirectory, "Legacy.Desktop.csproj"),
                "<Project><PropertyGroup><OutputType>WinExe</OutputType><AssemblyName>Legacy.Desktop</AssemblyName><ProjectTypeGuids>{60dc8134-eba5-43b8-bcc9-bb4bc16c2548}</ProjectTypeGuids></PropertyGroup></Project>");
            var executable = Path.Combine(projectDirectory, "bin", "Debug", "Legacy.Desktop.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);

            var applications = WpfWorkspacePolicyProvisioner.DiscoverApplications(temp);

            Assert.HasCount(1, applications);
            Assert.AreEqual("Legacy.Desktop.exe", applications[0].Name);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void IsWorkspaceRoot_RejectsDirectoryWithoutDotNetProjects()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Assert.IsFalse(WpfWorkspacePolicyProvisioner.IsWorkspaceRoot(temp));
            Assert.ThrowsExactly<WpfWorkspaceDiscoveryException>(() => WpfWorkspacePolicyProvisioner.CreatePolicy(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void GetDefaultPolicyPath_IsStableAndWorkspaceSpecific()
    {
        var first = CreateSyntheticWorkspace();
        var second = CreateSyntheticWorkspace();
        try
        {
            var firstPath = WpfWorkspacePolicyProvisioner.GetDefaultPolicyPath(first);
            var repeatedPath = WpfWorkspacePolicyProvisioner.GetDefaultPolicyPath(first);
            var secondPath = WpfWorkspacePolicyProvisioner.GetDefaultPolicyPath(second);

            Assert.AreEqual(firstPath, repeatedPath);
            Assert.AreNotEqual(firstPath, secondPath);
            StringAssert.Contains(firstPath, Path.Combine("EngineeringMcp", "policies"), StringComparison.OrdinalIgnoreCase);
            StringAssert.EndsWith(firstPath, ".json", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [TestMethod]
    public void DiscoverApplications_ReadsNearestDirectoryBuildPropsWithoutExecutingMsBuild()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "build"));
            File.WriteAllText(
                Path.Combine(temp, "Directory.Build.props"),
                "<Project><Import Project=\"build/wpf.props\"/></Project>");
            File.WriteAllText(
                Path.Combine(temp, "build", "wpf.props"),
                "<Project><PropertyGroup><UseWPF>true</UseWPF><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            var projectDirectory = Path.Combine(temp, "src", "Centralized.Client");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(
                Path.Combine(projectDirectory, "Centralized.Client.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Centralized.Client</AssemblyName></PropertyGroup></Project>");
            var executable = Path.Combine(projectDirectory, "bin", "Debug", "Centralized.Client.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);

            var applications = WpfWorkspacePolicyProvisioner.DiscoverApplications(temp);

            Assert.HasCount(1, applications);
            Assert.AreEqual("Centralized.Client.exe", applications[0].Name);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void DiscoverApplications_IgnoresImportThatLeavesWorkspace()
    {
        var parent = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(
                Path.Combine(parent, "external.props"),
                "<Project><PropertyGroup><UseWPF>true</UseWPF><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            var projectDirectory = Path.Combine(workspace, "Client");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(
                Path.Combine(projectDirectory, "Client.csproj"),
                "<Project><Import Project=\"../../external.props\"/><PropertyGroup><AssemblyName>Client</AssemblyName></PropertyGroup></Project>");
            var executable = Path.Combine(projectDirectory, "bin", "Debug", "Client.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, string.Empty);

            Assert.ThrowsExactly<WpfWorkspaceDiscoveryException>(
                () => WpfWorkspacePolicyProvisioner.DiscoverApplications(workspace));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [TestMethod]
    public void ProvisionExecutable_AuthorizesVerifiedWpfApplicationInsideWorkspace()
    {
        var sourceExecutable = FindFixtureExecutable();
        var sourceAssembly = Path.ChangeExtension(sourceExecutable, ".dll");
        var temp = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(temp, "bin");
        Directory.CreateDirectory(output);
        try
        {
            var executable = Path.Combine(output, Path.GetFileName(sourceExecutable));
            File.Copy(sourceExecutable, executable);
            File.Copy(sourceAssembly, Path.ChangeExtension(executable, ".dll"));

            var result = WpfWorkspacePolicyProvisioner.ProvisionExecutable(
                temp,
                executable,
                Path.Combine(temp, "policy.json"));

            Assert.HasCount(1, result.Applications);
            Assert.AreEqual(Path.GetFullPath(executable), result.Applications[0].ExecutablePath);
            Assert.IsNull(result.Applications[0].ProjectPath);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void ProvisionExecutable_RejectsNonWpfAndOutOfWorkspaceExecutables()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var nonWpf = Path.Combine(temp, "NotWpf.exe");
            File.Copy(typeof(WpfWorkspacePolicyProvisioner).Assembly.Location, nonWpf);
            Assert.ThrowsExactly<InvalidDataException>(
                () => WpfWorkspacePolicyProvisioner.ProvisionExecutable(temp, nonWpf));

            var outside = FindFixtureExecutable();
            Assert.ThrowsExactly<InvalidDataException>(
                () => WpfWorkspacePolicyProvisioner.ProvisionExecutable(temp, outside));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string CreateSyntheticWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "EngineeringMcp.WorkspacePolicyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "UniversalWpfWorkspace.sln"), string.Empty);

        CreateProject(root, "src", "Sample.Desktop", "Sample.Desktop", useWpf: true, outputType: "WinExe", createExecutable: true);
        CreateProject(root, "apps", "Workshop", "Workshop.Client", useWpf: true, outputType: "Exe", createExecutable: true);
        CreateProject(root, "tools", "Console.Utility", "Console.Utility", useWpf: false, outputType: "Exe", createExecutable: true);
        CreateProject(root, "src", "Visual.Components", "Visual.Components", useWpf: true, outputType: null, createExecutable: true);
        return root;
    }

    private static void CreateProject(
        string root,
        string group,
        string projectName,
        string assemblyName,
        bool useWpf,
        string? outputType,
        bool createExecutable)
    {
        var projectDirectory = Path.Combine(root, group, projectName);
        Directory.CreateDirectory(projectDirectory);
        var outputTypeElement = outputType is null ? string.Empty : $"<OutputType>{outputType}</OutputType>";
        File.WriteAllText(
            Path.Combine(projectDirectory, projectName + ".csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>{useWpf.ToString().ToLowerInvariant()}</UseWPF>{outputTypeElement}<AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>");

        if (!createExecutable) return;
        var executable = Path.Combine(projectDirectory, "bin", "Debug", "net10.0-windows", assemblyName + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, string.Empty);
    }

    private static string FindFixtureExecutable()
        => WpfTestFixtureLocator.FindExecutable();
}
