using System.Security.Cryptography;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed record WpfWorkspaceApplication(
    string Name,
    string ExecutablePath,
    string? ProjectPath);

public sealed class WpfWorkspaceDiscoveryException(string message) : Exception(message);

public sealed record WpfWorkspacePolicyProvisioningResult(
    string WorkspaceRoot,
    IReadOnlyList<WpfWorkspaceApplication> Applications,
    string PolicyPath);

public static partial class WpfWorkspacePolicyProvisioner
{
    private const int MaximumDirectories = 4096;
    private const int MaximumOutputDirectoriesPerProject = 512;
    private const int MaximumProjects = 256;
    private const int MaximumApplications = 64;
    private const int MaximumImportedProjectFiles = 32;
    private const long MaximumProjectFileBytes = 1024 * 1024;

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".idea", ".vs", "artifacts", "bin", "node_modules", "obj", "packages"
    };

    private static readonly string[] DenyGlobs =
    [
        "**/.env",
        "**/.env.*",
        "**/secrets.json",
        "**/appsettings.Production.json",
        "**/*.pfx",
        "**/*.p12",
        "**/*.pem",
        "**/*.key",
        "**/*.bak",
        "**/*.db",
        "**/*.dump",
        "**/*.dmp",
        "**/*.mdf",
        "**/*.ldf",
        "**/*.nettrace",
        "**/*.sqlite",
        "**/*.sqlite3",
        "**/.git/**"
    ];

    public static string GetDefaultPolicyPath(string workspaceRoot)
    {
        var root = NormalizeWorkspaceRoot(workspaceRoot);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new DirectoryNotFoundException("Windows local application-data directory is unavailable.");

        var workspaceName = Path.GetFileName(root);
        var safeName = SafePolicyNameRegex().Replace(workspaceName, "-").Trim('-');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "workspace";
        if (safeName.Length > 40) safeName = safeName[..40];

        var normalizedIdentity = root.ToUpperInvariant();
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIdentity)))
            .ToLowerInvariant()[..12];
        return Path.Combine(localAppData, "EngineeringMcp", "policies", $"policy.{safeName}.{pathHash}.json");
    }

    public static string? FindSuggestedWorkspaceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ENGINEERING_MCP_WORKSPACE_ROOT");
        foreach (var candidate in new[] { configured, Environment.CurrentDirectory })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && IsWorkspaceRoot(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    public static bool IsWorkspaceRoot(string workspaceRoot)
    {
        try
        {
            var root = NormalizeWorkspaceRoot(workspaceRoot);
            return EnumerateProjectFiles(root).Count > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidDataException or
                                   NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<WpfWorkspaceApplication> DiscoverApplications(string workspaceRoot)
    {
        var root = NormalizeWorkspaceRoot(workspaceRoot);
        var projectFiles = EnumerateProjectFiles(root);
        if (projectFiles.Count == 0)
            throw new WpfWorkspaceDiscoveryException("The selected workspace does not contain a discoverable .NET project.");

        var applications = new List<WpfWorkspaceApplication>();
        foreach (var projectPath in projectFiles)
        {
            var assemblyName = ReadWpfApplicationProject(projectPath, root);
            if (assemblyName is null) continue;

            var executable = FindNewestBuiltExecutable(projectPath, assemblyName);
            if (executable is null) continue;

            applications.Add(new WpfWorkspaceApplication(
                Path.GetFileName(executable),
                executable,
                projectPath));
            if (applications.Count > MaximumApplications)
                throw new InvalidDataException($"The workspace contains more than {MaximumApplications} built WPF applications. Use a narrower workspace root.");
        }

        if (applications.Count == 0)
            throw new WpfWorkspaceDiscoveryException(
                "No built WPF application was found automatically. Build a WPF project or select its executable explicitly.");

        var duplicateName = applications
            .GroupBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
            throw new InvalidDataException(
                $"Multiple WPF projects produce '{duplicateName.Key}'. Use distinct AssemblyName values or an explicit policy.");

        return applications
            .OrderBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(application => application.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static McpPolicy CreatePolicy(string workspaceRoot)
    {
        var root = NormalizeWorkspaceRoot(workspaceRoot);
        var applications = DiscoverApplications(root);
        return CreatePolicy(root, applications);
    }

    private static McpPolicy CreatePolicy(
        string root,
        IReadOnlyList<WpfWorkspaceApplication> applications)
    {
        var policy = new McpPolicy(
            PermissionLevel.ApplicationDiagnostics,
            new ProcessPolicy(applications
                .Select(application => new AllowedProcessRule(application.Name, application.ExecutablePath))
                .ToArray()),
            new FileSystemPolicy([root], DenyGlobs),
            new NetworkPolicy("deny", []),
            PiiMode.Mask,
            new AuditPolicy(Enabled: true, Directory: null, RetentionDays: 30),
            new ScreenshotPolicy(
                Enabled: true,
                MaskPasswordControls: true,
                MaskSensitiveNames: true,
                FailClosedOnRedactionError: true,
                MaskTextControls: true),
            new UiActionPolicy(
                DenyAutomationIds:
                [
                    "ApiTokenPasswordBox",
                    "ConnectionStringTextBox",
                    "PasswordBox",
                    "SecretTextBox"
                ],
                DestructiveAutomationIds: [],
                StatefulAutomationIds: []),
            AllowDestructiveActions: false,
            AllowPrivilegedDiagnostics: false,
            PolicyVersion: 1,
            EnabledToolProfiles: ["core", "wpf-read", "wpf-interact", "diagnostics", "source"]);

        PolicyValidator.Validate(policy);
        return policy;
    }

    public static WpfWorkspacePolicyProvisioningResult Provision(
        string workspaceRoot,
        string? destinationPath = null)
    {
        var root = NormalizeWorkspaceRoot(workspaceRoot);
        var applications = DiscoverApplications(root);
        return WritePolicy(root, applications, destinationPath);
    }

    public static WpfWorkspacePolicyProvisioningResult ProvisionExecutable(
        string workspaceRoot,
        string executablePath,
        string? destinationPath = null)
    {
        var root = NormalizeWorkspaceRoot(workspaceRoot);
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("An executable path is required.", nameof(executablePath));

        var executable = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
            throw new FileNotFoundException("Select an existing Windows executable.", executable);
        if (!IsPathContainedBy(root, executable))
            throw new InvalidDataException("The selected executable must remain inside the selected workspace.");
        EnsurePathHasNoReparsePoints(root, executable);
        if (!IsWpfExecutable(executable))
            throw new InvalidDataException(
                "The selected executable is not a verifiable managed WPF application. Select a WPF executable with its companion DLL present.");

        WpfWorkspaceApplication[] applications =
        [
            new(Path.GetFileName(executable), executable, ProjectPath: null)
        ];
        return WritePolicy(root, applications, destinationPath);
    }

    private static WpfWorkspacePolicyProvisioningResult WritePolicy(
        string root,
        IReadOnlyList<WpfWorkspaceApplication> applications,
        string? destinationPath)
    {
        var policy = CreatePolicy(root, applications);
        var policyPath = Path.GetFullPath(destinationPath ?? GetDefaultPolicyPath(root));
        var policyDirectory = Path.GetDirectoryName(policyPath)
            ?? throw new InvalidDataException("The generated policy path has no parent directory.");
        Directory.CreateDirectory(policyDirectory);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(policy, options) + Environment.NewLine;
        var temporaryPath = policyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, policyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new WpfWorkspacePolicyProvisioningResult(root, applications, policyPath);
    }

    private static string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("A workspace root is required.", nameof(workspaceRoot));

        var root = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The selected workspace directory does not exist.");

        var filesystemRoot = Path.GetPathRoot(root)?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, filesystemRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A workspace cannot be an entire drive or filesystem root.");

        return root;
    }

    private static IReadOnlyList<string> EnumerateProjectFiles(string root)
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

    private static string? ReadWpfApplicationProject(string projectPath, string workspaceRoot)
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
        while (current is not null && IsPathContainedByOrEqual(workspaceRoot, current.FullName))
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
        if (!IsPathContainedBy(workspaceRoot, fullPath) || !File.Exists(fullPath)) return;
        EnsurePathHasNoReparsePoints(workspaceRoot, fullPath);
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
                    if (IsPathContainedBy(workspaceRoot, importedPath))
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

    private static bool IsWpfExecutable(string executablePath)
    {
        if (!IsExecutableImage(executablePath)) return false;
        if (ReferencesWpfAssembly(executablePath)) return true;
        var companionAssembly = Path.ChangeExtension(executablePath, ".dll");
        return File.Exists(companionAssembly) &&
               IsAppHostBoundToCompanion(executablePath, companionAssembly) &&
               ReferencesWpfAssembly(companionAssembly);
    }

    private static bool IsExecutableImage(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var characteristics = peReader.PEHeaders.CoffHeader.Characteristics;
            return characteristics.HasFlag(Characteristics.ExecutableImage) &&
                   !characteristics.HasFlag(Characteristics.Dll);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAppHostBoundToCompanion(string executablePath, string companionAssembly)
    {
        const long maximumAppHostBytes = 64L * 1024 * 1024;
        var executable = new FileInfo(executablePath);
        if (executable.Length <= 0 || executable.Length > maximumAppHostBytes) return false;
        var expectedName = Encoding.UTF8.GetBytes(Path.GetFileName(companionAssembly));
        var image = File.ReadAllBytes(executablePath);
        return image.AsSpan().IndexOf(expectedName) >= 0;
    }

    private static bool ReferencesWpfAssembly(string assemblyPath)
    {
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata) return false;
            var metadata = peReader.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                if (name is "PresentationFramework" or "PresentationCore") return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsPathContainedBy(string root, string path) =>
        !string.Equals(root, path, StringComparison.OrdinalIgnoreCase) &&
        IsPathContainedByOrEqual(root, path);

    private static bool IsPathContainedByOrEqual(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
               (!Path.IsPathFullyQualified(relative) &&
                relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void EnsurePathHasNoReparsePoints(string root, string path)
    {
        if (!IsPathContainedByOrEqual(root, path))
            throw new InvalidDataException("The selected path leaves the workspace.");

        var current = new DirectoryInfo(root);
        if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Workspace reparse points cannot be authorized.");
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var currentPath = root;
        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            var attributes = File.GetAttributes(currentPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Paths containing reparse points cannot be authorized.");
        }
    }

    private static string? FindNewestBuiltExecutable(string projectPath, string assemblyName)
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

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SafePolicyNameRegex();
}
