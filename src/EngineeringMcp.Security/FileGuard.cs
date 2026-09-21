using System.Text.RegularExpressions;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed class FileGuard(FilePolicyProvider policyProvider)
{
    public ToolResult<string> RequireReadable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult<string>.Fail("INVALID_PATH", "Path is required.");

        var fullPath = Path.GetFullPath(path);

        var underRoot = policyProvider.Current.Filesystem.ReadRoots
            .Select(Path.GetFullPath)
            .Any(root => PathGuard.IsWithin(root, fullPath));

        if (!underRoot)
            return ToolResult<string>.Fail(
                "PATH_NOT_ALLOWED",
                "Path is outside configured read roots.",
                remediation: "Select an approved policy containing the narrowest required local repository root in filesystem.readRoots, then restart the MCP server.");

        if (PathGuard.ContainsReparsePoint(fullPath))
            return ToolResult<string>.Fail(
                "PATH_LINK_DENIED",
                "Symbolic links, junctions, and reparse points are denied to prevent approved-root escape.",
                remediation: "Use a real local path beneath an approved read root; do not route source access through a link or junction.");

        // Built-in sensitive rules are enforced even when the selected policy carries no denyGlobs,
        // so credential, key, dump, and database files can never be read through source tools.
        foreach (var pattern in SensitiveFileRules.DenyGlobs)
        {
            if (GlobMatches(fullPath, pattern))
                return ToolResult<string>.Fail(
                    "SENSITIVE_FILE_DENIED",
                    "Sensitive credential, key, dump, or trace files cannot be read through source tools.",
                    remediation: "Keep the file outside the MCP boundary. Use redacted metadata or a purpose-built safe diagnostic instead.");
        }

        foreach (var pattern in policyProvider.Current.Filesystem.DenyGlobs)
        {
            if (GlobMatches(fullPath, pattern))
                return ToolResult<string>.Fail(
                    "PATH_DENIED",
                    "Path matches a configured deny rule.",
                    remediation: "Keep the denial unless the rule is demonstrably incorrect; use a non-sensitive file or obtain approval for a narrowly scoped policy correction.");
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            return ToolResult<string>.Fail(
                "PATH_NOT_FOUND",
                "Path does not exist.",
                remediation: "Refresh the repository path and retry with an existing local file or directory.");

        return ToolResult<string>.Ok(fullPath);
    }

    internal static bool GlobMatches(string path, string pattern)
    {
        var normalizedPath = path.Replace('\\', '/');
        var normalizedPattern = pattern.Replace('\\', '/');
        var regex = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";

        if (!normalizedPattern.Contains('/'))
            regex = "(^|.*/)" + regex.TrimStart('^');
        else if (normalizedPattern.StartsWith("**/", StringComparison.Ordinal))
            regex = "^(?:.*/)?" + Regex.Escape(normalizedPattern[3..])
                .Replace(@"\*\*", ".*", StringComparison.Ordinal)
                .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
                .Replace(@"\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(normalizedPath, regex, OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }
}
