using System.Xml;
using System.Xml.Linq;

namespace EngineeringMcp.Security;

/// <summary>
/// Bounded static scanning of .NET project files for workspace authorization. Discovery never
/// executes MSBuild: it parses csproj/props XML with DTDs prohibited, follows only safe literal
/// imports inside the workspace, and enforces hard directory/file ceilings.
/// </summary>
internal static class WorkspaceProjectScanner
{
    internal const int MaximumDirectories = 4096;
    internal const int MaximumOutputDirectoriesPerProject = 512;
    internal const int MaximumProjects = 256;
    internal const int MaximumImportedProjectFiles = 32;
    internal const long MaximumProjectFileBytes = 1024 * 1024;

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "artifacts", "bin", "node_modules", "obj", "packages"
    };

    internal static IReadOnlyList<string> EnumerateProjectFiles(string root)
    {
        var projects = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        var visitedDirectories = 0;

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            visitedDirectories++;
            if (visitedDirectories > MaximumDirectories)
                throw new InvalidDataException($"Workspace discovery exceeded {MaximumDirectories} directories. Use a narrower workspace root.");

            try
            {
                foreach (var project in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
                {
                    projects.Add(Path.GetFullPath(project));
                    if (projects.Count > MaximumProjects)
                        throw new InvalidDataException($"Workspace discovery found more than {MaximumProjects} projects. Use a narrower workspace root.");
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var info = new DirectoryInfo(child);
                    if (ExcludedDirectoryNames.Contains(info.Name) ||
                        info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    directories.Push(info.FullName);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Inaccessible subdirectories are outside the discoverable workspace surface.
            }
        }

        return projects.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string? ReadWpfApplicationProject(string projectPath, string workspaceRoot)
    {
        try
        {
            var elements = ReadStaticProjectElements(projectPath, workspaceRoot);
            var properties = elements
                .Where(element => element.Name.LocalName is "UseWPF" or "OutputType" or "AssemblyName" or "ProjectTypeGuids")
                .ToArray();
            var usesWpfProperty = properties
                .Where(element => element.Name.LocalName == "UseWPF")
                .Select(element => element.Value.Trim())
                .LastOrDefault() is { } useWpfValue &&
                string.Equals(useWpfValue, "true", StringComparison.OrdinalIgnoreCase);
            var usesClassicWpfProjectType = properties
                .Where(element => element.Name.LocalName == "ProjectTypeGuids")
                .Any(element => element.Value.Contains("60dc8134-eba5-43b8-bcc9-bb4bc16c2548", StringComparison.OrdinalIgnoreCase));
            var referencesPresentationFramework = elements
                .Where(element => element.Name.LocalName == "Reference")
                .Select(element => element.Attribute("Include")?.Value)
                .Any(value => value?.StartsWith("PresentationFramework", StringComparison.OrdinalIgnoreCase) is true);
            var hasApplicationDefinition = elements.Any(element => element.Name.LocalName == "ApplicationDefinition");
            var useWpf = usesWpfProperty || usesClassicWpfProjectType ||
                         (referencesPresentationFramework && hasApplicationDefinition);
            var outputType = properties
                .Where(element => element.Name.LocalName == "OutputType")
                .Select(element => element.Value.Trim())
                .LastOrDefault(value => value.Length > 0);
            var isExecutable = string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase);
            if (!useWpf || !isExecutable) return null;

            var assemblyName = properties
                .Where(element => element.Name.LocalName == "AssemblyName")
                .Select(element => element.Value.Trim())
                .LastOrDefault(value => value.Length > 0 && !value.Contains("$(", StringComparison.Ordinal))
                ?? Path.GetFileNameWithoutExtension(projectPath);
            return assemblyName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            return null;
        }
    }

    internal static string? FindNewestBuiltExecutable(string projectPath, string assemblyName)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException("A project path has no parent directory.");
        var binDirectory = Path.Combine(projectDirectory, "bin");
        if (!Directory.Exists(binDirectory)) return null;

        FileInfo? newest = null;
        var directories = new Stack<string>();
        directories.Push(binDirectory);
        var visitedDirectories = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            visitedDirectories++;
            if (visitedDirectories > MaximumOutputDirectoriesPerProject)
                throw new InvalidDataException(
                    $"Build-output discovery exceeded {MaximumOutputDirectoriesPerProject} directories for one WPF project.");

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, assemblyName + ".exe", SearchOption.TopDirectoryOnly))
                {
                    var candidate = new FileInfo(path);
                    if (candidate.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (newest is null || candidate.LastWriteTimeUtc > newest.LastWriteTimeUtc ||
                        (candidate.LastWriteTimeUtc == newest.LastWriteTimeUtc &&
                         string.Compare(candidate.FullName, newest.FullName, StringComparison.OrdinalIgnoreCase) < 0))
                        newest = candidate;
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var info = new DirectoryInfo(child);
                    if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        directories.Push(info.FullName);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Inaccessible build directories cannot become authorized process targets.
            }
        }

        return newest?.FullName;
    }

    private static IReadOnlyList<XElement> ReadStaticProjectElements(string projectPath, string workspaceRoot)
    {
        var elements = new List<XElement>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException("A project path has no parent directory.");
        var directoryBuildProps = FindNearestDirectoryBuildProps(projectDirectory, workspaceRoot);
        if (directoryBuildProps is not null)
            ReadStaticProjectFile(directoryBuildProps, workspaceRoot, visited, elements);
        ReadStaticProjectFile(projectPath, workspaceRoot, visited, elements);
        return elements;
    }

    private static string? FindNearestDirectoryBuildProps(string projectDirectory, string workspaceRoot)
    {
        var current = new DirectoryInfo(projectDirectory);
        while (current is not null && PathGuard.IsWithin(workspaceRoot, current.FullName))
        {
            var candidate = Path.Combine(current.FullName, "Directory.Build.props");
            if (File.Exists(candidate)) return candidate;
            if (string.Equals(current.FullName, workspaceRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }
        return null;
    }

    private static void ReadStaticProjectFile(
        string filePath,
        string workspaceRoot,
        HashSet<string> visited,
        List<XElement> elements)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!PathGuard.IsStrictlyWithin(workspaceRoot, fullPath) || !File.Exists(fullPath)) return;
        PathGuard.EnsureNoReparsePoints(workspaceRoot, fullPath);
        if (!visited.Add(fullPath)) return;
        if (visited.Count > MaximumImportedProjectFiles)
            throw new InvalidDataException($"Static project discovery exceeded {MaximumImportedProjectFiles} project files.");
        if (new FileInfo(fullPath).Length > MaximumProjectFileBytes)
            throw new InvalidDataException("A project file exceeds the safe static-discovery size limit.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumProjectFileBytes
        };
        using var reader = XmlReader.Create(fullPath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var baseDirectory = Path.GetDirectoryName(fullPath)!;
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName == "Import" && IsUnconditional(element))
            {
                var import = element.Attribute("Project")?.Value.Trim();
                if (IsSafeLiteralImport(import))
                {
                    var importedPath = Path.GetFullPath(Path.Combine(baseDirectory, import!));
                    if (PathGuard.IsStrictlyWithin(workspaceRoot, importedPath))
                        ReadStaticProjectFile(importedPath, workspaceRoot, visited, elements);
                }
                continue;
            }

            if (IsRelevantStaticElement(element) && IsUnconditional(element))
                elements.Add(element);
        }
    }

    private static bool IsRelevantStaticElement(XElement element) =>
        element.Name.LocalName is "UseWPF" or "OutputType" or "AssemblyName" or "ProjectTypeGuids" or
            "Reference" or "ApplicationDefinition";

    private static bool IsUnconditional(XElement element) =>
        !element.AncestorsAndSelf().Any(ancestor =>
            ancestor.Attribute("Condition") is not null || ancestor.Name.LocalName == "Target");

    private static bool IsSafeLiteralImport(string? import) =>
        !string.IsNullOrWhiteSpace(import) &&
        !Path.IsPathFullyQualified(import) &&
        import.IndexOfAny(['*', '?']) < 0 &&
        !import.Contains("$(", StringComparison.Ordinal) &&
        !import.Contains("@(", StringComparison.Ordinal) &&
        !import.Contains("%(", StringComparison.Ordinal);
}
