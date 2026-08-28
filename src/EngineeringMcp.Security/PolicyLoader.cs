using System.Text.Json;
using System.Text.Json.Serialization;
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
            }
        };
        PolicyValidator.Validate(Current);
        Source = fullPath;
    }
}

public static class PolicyValidator
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
        if (policy.EnabledTools is { Count: > 0 } && policy.DisabledTools is { Count: > 0 })
        {
            var overlap = policy.EnabledTools.Intersect(policy.DisabledTools, StringComparer.Ordinal).FirstOrDefault();
            if (overlap is not null)
                throw new InvalidDataException($"Tool '{overlap}' cannot be both enabled and disabled.");
        }

        foreach (var root in policy.Filesystem.ReadRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidDataException("filesystem.readRoots entries must be fully-qualified paths.");
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A filesystem read root cannot be an entire drive or filesystem root.");
        }
    }
}
