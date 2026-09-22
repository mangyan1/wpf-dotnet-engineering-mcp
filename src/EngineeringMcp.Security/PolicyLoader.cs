using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public class FilePolicyProvider
{
    public virtual McpPolicy Current { get; }
    public virtual string Source { get; }

    public FilePolicyProvider()
        : this(Environment.GetEnvironmentVariable("ENGINEERING_MCP_POLICY"), allowLockedDownDefault: true)
    {
    }

    public FilePolicyProvider(string policyPath)
        : this(policyPath, allowLockedDownDefault: false)
    {
    }

    private FilePolicyProvider(string? path, bool allowLockedDownDefault)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!allowLockedDownDefault)
                throw new ArgumentException("A policy path is required.", nameof(path));
            Current = McpPolicy.LockedDownDefault;
            Source = "locked-down-default";
            return;
        }

        var fullPath = Path.GetFullPath(path);
        // Only check for reparse points when the policy file exists, so a missing file surfaces the
        // real file-not-found error instead of a misleading reparse-point failure.
        if (File.Exists(fullPath) && PathGuard.ContainsReparsePoint(fullPath))
            throw new InvalidDataException("The policy file path contains a reparse point; symbolic links and junctions are denied for policy loading.");
        var json = File.ReadAllText(fullPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var deserialized = JsonSerializer.Deserialize<McpPolicy>(json, options)
            ?? throw new InvalidDataException("Policy file deserialized to null.");
        var policyDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("Policy file has no parent directory.");
        Current = deserialized with
        {
            Filesystem = deserialized.Filesystem with
            {
                ReadRoots = deserialized.Filesystem.ReadRoots
                    .Select(root => Path.GetFullPath(root, policyDirectory))
                    .ToArray()
            },
            // Relative process paths resolve against the policy file directory, mirroring readRoots,
            // so matching does not depend on the host process working directory.
            Processes = new ProcessPolicy(deserialized.Processes.Allow
                .Select(rule => rule with
                {
                    Path = string.IsNullOrWhiteSpace(rule.Path) ? rule.Path : Path.GetFullPath(rule.Path, policyDirectory)
                })
                .ToArray())
        };
        PolicyValidator.Validate(Current);
        Source = fullPath;
    }
}

public static partial class PolicyValidator
{
    private static readonly HashSet<string> KnownProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "core", "wpf-read", "wpf-interact", "diagnostics", "source"
    };

    public static void Validate(McpPolicy policy)
    {
        if (policy.PolicyVersion != 1)
            throw new InvalidDataException($"Unsupported policyVersion '{policy.PolicyVersion}'. Expected version 1.");
        if (policy.Processes?.Allow is null || policy.Filesystem?.ReadRoots is null || policy.Filesystem.DenyGlobs is null ||
            policy.Network?.Allow is null || policy.Audit is null || policy.Screenshots is null || policy.UiActions is null)
            throw new InvalidDataException("Policy is missing one or more required sections or collections.");
        if (!string.Equals(policy.Network.Default, "deny", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("network.default must be 'deny'; this local MCP server has no open-world egress tools.");
        if (policy.Pii == PiiMode.Off)
            throw new InvalidDataException("pii 'Off' is not permitted. Use Mask, Hash, or Remove.");
        if (policy.Audit.RetentionDays is < 1 or > 3650)
            throw new InvalidDataException("audit.retentionDays must be between 1 and 3650.");
        if (!string.IsNullOrWhiteSpace(policy.Audit.Directory) && !Path.IsPathFullyQualified(policy.Audit.Directory))
            throw new InvalidDataException("audit.directory must be an absolute path when configured.");
        if (policy.Screenshots.Enabled && !policy.Screenshots.FailClosedOnRedactionError)
            throw new InvalidDataException("Enabled screenshots must fail closed when redaction fails.");
        if (policy.EnabledToolProfiles is { Count: > 0 })
        {
            var unknown = policy.EnabledToolProfiles.Where(profile => !KnownProfiles.Contains(profile)).ToArray();
            if (unknown.Length > 0)
                throw new InvalidDataException($"Unknown tool profile(s): {string.Join(", ", unknown)}.");
        }

        var duplicateRule = policy.Processes.Allow.GroupBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateRule is not null)
            throw new InvalidDataException($"Duplicate process allow rule for '{duplicateRule.Key}'.");
        foreach (var rule in policy.Processes.Allow)
        {
            if (!string.IsNullOrWhiteSpace(rule.Sha256) && !Sha256Regex().IsMatch(rule.Sha256))
                throw new InvalidDataException("processes.allow sha256 values must contain exactly 64 hexadecimal characters.");
            if (string.IsNullOrWhiteSpace(rule.Path) && string.IsNullOrWhiteSpace(rule.Sha256))
                throw new InvalidDataException(
                    $"Process allow rule '{rule.Name}' must specify an exact executable path or sha256; a name-only rule would match any executable of that name.");
        }
        if (policy.EnabledTools is { Count: > 0 } && policy.DisabledTools is { Count: > 0 })
        {
            var overlap = policy.EnabledTools.Intersect(policy.DisabledTools, StringComparer.Ordinal).FirstOrDefault();
            if (overlap is not null)
                throw new InvalidDataException($"Tool '{overlap}' cannot be both enabled and disabled.");
        }
        foreach (var toolName in (policy.EnabledTools ?? []).Concat(policy.DisabledTools ?? []))
        {
            if (toolName is null || !ToolNameRegex().IsMatch(toolName))
                throw new InvalidDataException("enabledTools and disabledTools entries must match ^[a-z0-9_-]+$.");
        }
        if (policy.UiActions.DenyAutomationIds is null || policy.UiActions.DenyAutomationIds.Any(string.IsNullOrWhiteSpace) ||
            policy.UiActions.DestructiveAutomationIds is null || policy.UiActions.DestructiveAutomationIds.Any(string.IsNullOrWhiteSpace) ||
            policy.UiActions.StatefulAutomationIds is null || policy.UiActions.StatefulAutomationIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("uiActions automationId entries must be non-empty.");

        foreach (var root in policy.Filesystem.ReadRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidDataException("filesystem.readRoots entries must be fully-qualified paths.");
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A filesystem read root cannot be an entire drive or filesystem root.");
        }
    }

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNameRegex();
}
