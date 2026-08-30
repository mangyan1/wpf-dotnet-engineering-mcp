using EngineeringMcp.Contracts;

namespace EngineeringMcp.IntegrationTests;

internal static class WpfTestFixtureLocator
{
    private const string ProjectName = "EngineeringMcp.Wpf.TestApp";

    public static string FindExecutable()
    {
        var artifacts = Environment.GetEnvironmentVariable(McpRuntimeDefaults.ArtifactsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(artifacts))
            return Path.Combine(Path.GetFullPath(artifacts), "bin", ProjectName, "debug", ProjectName + ".exe");

        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var activeConfiguration = testOutput.Parent?.Name;
        var configurations = new[] { activeConfiguration, "Release", "Debug" }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var root = TestRepositoryLocator.FindRoot();
        foreach (var configuration in configurations)
        {
            var candidate = Path.Combine(root, "tests", ProjectName, "bin", configuration!,
                "net10.0-windows10.0.19041.0", ProjectName + ".exe");
            if (File.Exists(candidate)) return candidate;
        }

        return Path.Combine(root, "tests", ProjectName, "bin", activeConfiguration ?? "Debug",
            "net10.0-windows10.0.19041.0", ProjectName + ".exe");
    }
}
