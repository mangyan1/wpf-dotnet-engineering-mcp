using EngineeringMcp.Contracts;

namespace EngineeringMcp.IntegrationTests;

internal static class TestArtifactLocator
{
    private const string HostProjectName = "EngineeringMcp.Host";

    public static string FindHostExecutable(
        string repositoryRoot,
        string configuration,
        string targetFramework)
        => FindHostExecutable(
            repositoryRoot,
            Environment.GetEnvironmentVariable(McpRuntimeDefaults.ArtifactsPathEnvironmentVariable),
            configuration,
            targetFramework);

    internal static string FindHostExecutable(
        string repositoryRoot,
        string? artifactsPath,
        string configuration,
        string targetFramework)
    {
        if (!string.IsNullOrWhiteSpace(artifactsPath))
        {
            return Path.Combine(
                Path.GetFullPath(artifactsPath),
                "bin",
                HostProjectName,
                configuration.ToLowerInvariant(),
                HostProjectName + ".exe");
        }

        return Path.Combine(
            Path.GetFullPath(repositoryRoot),
            "src",
            HostProjectName,
            "bin",
            configuration,
            targetFramework,
            HostProjectName + ".exe");
    }
}
