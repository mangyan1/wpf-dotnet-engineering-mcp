using System.IO;

namespace EngineeringMcp.ControlCenter;

internal sealed class SelfTestArtifactLayout
{
    public const string Configuration = "Debug";

    private static readonly string SelfTestRoot = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "DotNetEngineeringMcp", "selftest"));

    private SelfTestArtifactLayout(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static SelfTestArtifactLayout Create()
    {
        var runName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var root = Path.GetFullPath(Path.Combine(SelfTestRoot, runName));
        EnsureContained(root);
        Directory.CreateDirectory(root);
        return new SelfTestArtifactLayout(root);
    }

    public ProjectLayout ApplyTo(ProjectLayout layout)
    {
        var configurationDirectory = Configuration.ToLowerInvariant();
        return layout with
        {
            HostExecutable = Executable("EngineeringMcp.Host", configurationDirectory),
            FixtureExecutable = Executable("EngineeringMcp.Wpf.TestApp", configurationDirectory),
            AspNetFixtureExecutable = Executable("EngineeringMcp.AspNetCore.TestApp", configurationDirectory)
        };
    }

    public bool TryDelete(out string? error)
    {
        try
        {
            EnsureContained(Root);
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private string Executable(string projectName, string configurationDirectory)
        => Path.Combine(Root, "bin", projectName, configurationDirectory, projectName + ".exe");

    private static void EnsureContained(string path)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(SelfTestRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Self-test artifact path escaped its dedicated temporary directory.");
    }
}
