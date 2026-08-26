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
    string VsCodeDoc)
{
    public static ProjectLayout Discover()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                var sln = Path.Combine(current.FullName, "DotNetEngineeringMcp.sln");
                if (File.Exists(sln))
                {
                    var root = current.FullName;
                    var defaultPolicy = Path.Combine(root, "config", "policy.vscode-test.json");
                    var configuredPolicy = Environment.GetEnvironmentVariable("ENGINEERING_MCP_POLICY");
                    var policy = string.IsNullOrWhiteSpace(configuredPolicy)
                        ? defaultPolicy
                        : Path.GetFullPath(configuredPolicy);

                    return new ProjectLayout(
                        root,
                        sln,
                        Path.Combine(root, "src", "EngineeringMcp.Host", "EngineeringMcp.Host.csproj"),
                        Path.Combine(root, "src", "EngineeringMcp.Host", "bin", "Debug", "net10.0-windows10.0.19041.0", "EngineeringMcp.Host.exe"),
                        Path.Combine(root, "tests", "EngineeringMcp.Wpf.TestApp", "EngineeringMcp.Wpf.TestApp.csproj"),
                        Path.Combine(root, "tests", "EngineeringMcp.Wpf.TestApp", "bin", "Debug", "net10.0-windows10.0.19041.0", "EngineeringMcp.Wpf.TestApp.exe"),
                        Path.Combine(root, "tests", "EngineeringMcp.AspNetCore.TestApp", "EngineeringMcp.AspNetCore.TestApp.csproj"),
                        Path.Combine(root, "tests", "EngineeringMcp.AspNetCore.TestApp", "bin", "Debug", "net10.0", "EngineeringMcp.AspNetCore.TestApp.exe"),
                        Path.Combine(root, "DotNetEngineeringMcp.code-workspace"),
                        Path.Combine(root, ".vscode", "mcp.json"),
                        Path.Combine(root, ".mcp.json"),
                        policy,
                        Path.Combine(root, "docs", "SECURITY.md"),
                        Path.Combine(root, "docs", "VSCODE.md"));
                }
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate DotNetEngineeringMcp.sln. Start Control Center from inside the repository.");
    }
}
