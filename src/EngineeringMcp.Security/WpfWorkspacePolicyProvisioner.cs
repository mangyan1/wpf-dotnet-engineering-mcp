using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

/// <summary>
/// Orchestrates WPF workspace authorization: bounded discovery (via <see cref="WorkspaceProjectScanner"/>),
/// executable verification (via <see cref="PeInspection"/>), and atomic provisioning of a validated
/// policy whose process rules pin the discovered executable path and SHA-256.
/// </summary>
public static partial class WpfWorkspacePolicyProvisioner
{
    private const int MaximumApplications = 64;

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
            return WorkspaceProjectScanner.EnumerateProjectFiles(root).Count > 0;
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
        var projectFiles = WorkspaceProjectScanner.EnumerateProjectFiles(root);
        if (projectFiles.Count == 0)
            throw new WpfWorkspaceDiscoveryException("The selected workspace does not contain a discoverable .NET project.");

        var applications = new List<WpfWorkspaceApplication>();
        foreach (var projectPath in projectFiles)
        {
            var assemblyName = WorkspaceProjectScanner.ReadWpfApplicationProject(projectPath, root);
            if (assemblyName is null) continue;

            var executable = WorkspaceProjectScanner.FindNewestBuiltExecutable(projectPath, assemblyName);
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
                // Pin the discovered executable's SHA-256 at provision time so ProcessGuard verifies
                // the exact binary, not merely its name and path. Rebuilt binaries require re-provisioning.
                .Select(application => new AllowedProcessRule(
                    application.Name,
                    application.ExecutablePath,
                    ProcessGuard.HashFile(application.ExecutablePath)))
                .ToArray()),
            new FileSystemPolicy([root], SensitiveFileRules.DenyGlobs),
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
        if (!PathGuard.IsStrictlyWithin(root, executable))
            throw new InvalidDataException("The selected executable must remain inside the selected workspace.");
        PathGuard.EnsureNoReparsePoints(root, executable);
        if (!PeInspection.IsWpfExecutable(executable))
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

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SafePolicyNameRegex();
}
