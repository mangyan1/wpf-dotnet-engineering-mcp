using System.IO;

namespace EngineeringMcp.ControlCenter;

internal sealed record ProjectLayout(
    string Root,
    string Solution,
    string HostProject,
    string HostExecutable,
    string FixtureProject,
    string FixtureExecutable,
    string AspNetFixtureProject,
    string AspNetFixtureExecutable,
    string WorkspaceFile,
    string WorkspaceMcpConfig,
    string PortableMcpConfig,
    string Policy,
    string SecurityDoc,
    string VsCodeDoc,
    bool IsRepositoryMode)
{
    private const string PackageManifestName = "app-manifest.json";

    public bool SupportsDeveloperValidation => IsRepositoryMode;
    public string ModeLabel => IsRepositoryMode ? "Developer" : "Standalone";

    public static ProjectLayout Discover()
    {
        var applicationRoot = Path.GetFullPath(AppContext.BaseDirectory);
        if (File.Exists(Path.Combine(applicationRoot, PackageManifestName)))
            return DiscoverPackaged(applicationRoot);

        foreach (var start in new[] { Environment.CurrentDirectory, applicationRoot })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var solution = Path.Combine(current.FullName, "DotNetEngineeringMcp.sln");
                if (File.Exists(solution))
                    return DiscoverRepository(current.FullName, solution);
                current = current.Parent;
            }
        }

        if (File.Exists(Path.Combine(applicationRoot, "host", "EngineeringMcp.Host.exe")))
            return DiscoverPackaged(applicationRoot);

        throw new DirectoryNotFoundException(
            "Could not locate a source checkout or packaged Engineering MCP runtime. " +
            "Run from the repository or publish with build/release-hardening.ps1.");
    }

    private static ProjectLayout DiscoverRepository(string root, string solution)
        => new(
            root,
            solution,
            Path.Combine(root, "src", "EngineeringMcp.Host", "EngineeringMcp.Host.csproj"),
            Path.Combine(root, "src", "EngineeringMcp.Host", "bin", "Debug", "net10.0-windows10.0.19041.0", "EngineeringMcp.Host.exe"),
            Path.Combine(root, "tests", "EngineeringMcp.Wpf.TestApp", "EngineeringMcp.Wpf.TestApp.csproj"),
            Path.Combine(root, "tests", "EngineeringMcp.Wpf.TestApp", "bin", "Debug", "net10.0-windows10.0.19041.0", "EngineeringMcp.Wpf.TestApp.exe"),
            Path.Combine(root, "tests", "EngineeringMcp.AspNetCore.TestApp", "EngineeringMcp.AspNetCore.TestApp.csproj"),
            Path.Combine(root, "tests", "EngineeringMcp.AspNetCore.TestApp", "bin", "Debug", "net10.0", "EngineeringMcp.AspNetCore.TestApp.exe"),
            Path.Combine(root, "DotNetEngineeringMcp.code-workspace"),
            Path.Combine(root, ".vscode", "mcp.json"),
            Path.Combine(root, ".mcp.json"),
            ResolvePolicy(Path.Combine(root, "config", "policy.vscode-test.json")),
            Path.Combine(root, "docs", "SECURITY.md"),
            Path.Combine(root, "docs", "VSCODE.md"),
            IsRepositoryMode: true);

    private static ProjectLayout DiscoverPackaged(string root)
    {
        root = Path.GetFullPath(root);
        return new ProjectLayout(
            root,
            string.Empty,
            string.Empty,
            Path.Combine(root, "host", "EngineeringMcp.Host.exe"),
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            Path.Combine(root, ".mcp.json"),
            ResolvePolicy(Path.Combine(root, "config", "policy.packaged.json")),
            Path.Combine(root, "docs", "SECURITY.md"),
            Path.Combine(root, "docs", "VSCODE.md"),
            IsRepositoryMode: false);
    }

    private static string ResolvePolicy(string defaultPolicy)
    {
        var configuredPolicy = Environment.GetEnvironmentVariable("ENGINEERING_MCP_POLICY");
        return string.IsNullOrWhiteSpace(configuredPolicy)
            ? Path.GetFullPath(defaultPolicy)
            : Path.GetFullPath(configuredPolicy);
    }
}
