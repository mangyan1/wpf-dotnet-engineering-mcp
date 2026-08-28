using EngineeringMcp.Contracts;

namespace EngineeringMcp.IntegrationTests;

internal static class TestRepositoryLocator
{
    public static string FindRoot()
        => FindRoot(
            Environment.GetEnvironmentVariable(McpRuntimeDefaults.RepositoryRootEnvironmentVariable),
            Environment.CurrentDirectory,
            AppContext.BaseDirectory);

    internal static string FindRoot(string? configuredRoot, params string[] fallbackStarts)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var root = Path.GetFullPath(configuredRoot);
            if (HasSolution(root))
                return root;

            throw new DirectoryNotFoundException(
                $"{McpRuntimeDefaults.RepositoryRootEnvironmentVariable} does not identify a DotNetEngineeringMcp repository.");
        }

        foreach (var start in fallbackStarts.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(start)); current is not null; current = current.Parent)
            {
                if (HasSolution(current.FullName))
                    return current.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate DotNetEngineeringMcp.sln. Set {McpRuntimeDefaults.RepositoryRootEnvironmentVariable} for isolated test outputs.");
    }

    private static bool HasSolution(string directory)
        => File.Exists(Path.Combine(directory, "DotNetEngineeringMcp.sln"));
}
