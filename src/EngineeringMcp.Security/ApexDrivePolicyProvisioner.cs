using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed record ApexDrivePolicyProvisioningResult(
    string RepositoryRoot,
    string WorkstationExecutable,
    string PolicyPath);

public static class ApexDrivePolicyProvisioner
{
    private const string SolutionName = "ApexDrivePlatform.sln";
    private static readonly string ShellProjectRelativePath = Path.Combine(
        "src",
        "WorkstationClient",
        "ApexDrive.Workstation.Shell",
        "ApexDrive.Workstation.Shell.csproj");
    private static readonly string DebugShellRelativePath = Path.Combine(
        "src",
        "WorkstationClient",
        "ApexDrive.Workstation.Shell",
        "bin",
        "Debug",
        "net10.0-windows10.0.19041.0",
        "ApexDrive.Workstation.Shell.exe");
    private static readonly string ReleaseShellRelativePath = Path.Combine(
        "src",
        "WorkstationClient",
        "ApexDrive.Workstation.Shell",
        "bin",
        "Release",
        "net10.0-windows10.0.19041.0",
        "ApexDrive.Workstation.Shell.exe");

    private static readonly string[] DenyGlobs =
    [
        "**/.env",
        "**/secrets.json",
        "**/appsettings.Production.json",
        "**/*.pfx",
        "**/*.p12",
        "**/*.pem",
        "**/*.key",
        "**/*.dmp",
        "**/*.dump",
        "**/*.nettrace",
        "**/.git/**"
    ];

    public static string GetDefaultPolicyPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new DirectoryNotFoundException("Windows local application-data directory is unavailable.");

        return Path.Combine(localAppData, "EngineeringMcp", "policy.apexdrive.json");
    }

    public static string? FindSuggestedRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("APEXDRIVE_ROOT");
        var candidates = new[]
        {
            configured,
            @"D:\ApexDrive",
            @"C:\ApexDrive"
        };

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate!))
            .FirstOrDefault(IsRepositoryRoot);
    }

    public static bool IsRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return false;

        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            return File.Exists(Path.Combine(root, SolutionName))
                && File.Exists(Path.Combine(root, ShellProjectRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static McpPolicy CreatePolicy(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!IsRepositoryRoot(root))
            throw new InvalidDataException(
                $"The selected directory must contain {SolutionName} and the ApexDrive workstation shell project.");

        var debugShell = Path.Combine(root, DebugShellRelativePath);
        var releaseShell = Path.Combine(root, ReleaseShellRelativePath);
        var selectedShell = File.Exists(debugShell)
            ? debugShell
            : File.Exists(releaseShell)
                ? releaseShell
                : debugShell;

        var policy = new McpPolicy(
            PermissionLevel.ApplicationDiagnostics,
            new ProcessPolicy(
            [
                new AllowedProcessRule(
                    "ApexDrive.Workstation.Shell.exe",
                    selectedShell)
            ]),
            new FileSystemPolicy([root], DenyGlobs),
            new NetworkPolicy("deny", []),
            PiiMode.Mask,
            new AuditPolicy(Enabled: true, Directory: null, RetentionDays: 30),
            new ScreenshotPolicy(
                Enabled: false,
                MaskPasswordControls: true,
                MaskSensitiveNames: true,
                FailClosedOnRedactionError: true),
            new UiActionPolicy(
                DenyAutomationIds: ["ApiTokenPasswordBox"],
                DestructiveAutomationIds: [],
                StatefulAutomationIds: []),
            AllowDestructiveActions: false,
            AllowPrivilegedDiagnostics: false,
            PolicyVersion: 1,
            EnabledToolProfiles: ["core", "wpf-read", "wpf-interact", "diagnostics", "source"]);

        PolicyValidator.Validate(policy);
        return policy;
    }

    public static ApexDrivePolicyProvisioningResult Provision(
        string repositoryRoot,
        string? destinationPath = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var policy = CreatePolicy(root);
        var policyPath = Path.GetFullPath(destinationPath ?? GetDefaultPolicyPath());
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

        var workstation = policy.Processes.Allow.Single().Path
            ?? throw new InvalidDataException("The generated ApexDrive process rule has no executable path.");
        return new ApexDrivePolicyProvisioningResult(root, workstation, policyPath);
    }
}
