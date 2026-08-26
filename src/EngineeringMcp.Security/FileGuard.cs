using System.Text.RegularExpressions;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public interface IFileGuard
{
    ToolResult<string> RequireReadable(string path);
}

public sealed class FileGuard(IPolicyProvider policyProvider) : IFileGuard
{
    private static readonly string[] BuiltInSensitiveFileNames =
    {
        ".env", ".env.local", ".env.production", "secrets.json", "appsettings.production.json"
    };

    private static readonly string[] BuiltInSensitiveExtensions =
    {
        ".pfx", ".p12", ".pem", ".key", ".snk", ".dmp", ".dump", ".nettrace"
    };

    public ToolResult<string> RequireReadable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult<string>.Fail("INVALID_PATH", "Path is required.");

        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var underRoot = policyProvider.Current.Filesystem.ReadRoots
            .Select(Path.GetFullPath)
            .Any(root => IsUnderRoot(fullPath, root, comparison));

        if (!underRoot)
            return ToolResult<string>.Fail("PATH_NOT_ALLOWED", "Path is outside configured read roots.");

        if (ContainsReparsePoint(fullPath))
            return ToolResult<string>.Fail("PATH_LINK_DENIED", "Symbolic links, junctions, and reparse points are denied to prevent approved-root escape.");

        var fileName = Path.GetFileName(fullPath);
        if (BuiltInSensitiveFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase) ||
            BuiltInSensitiveExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
            return ToolResult<string>.Fail("SENSITIVE_FILE_DENIED", "Sensitive credential, key, dump, or trace files cannot be read through source tools.");

        foreach (var pattern in policyProvider.Current.Filesystem.DenyGlobs)
        {
            if (GlobMatches(fullPath, pattern))
                return ToolResult<string>.Fail("PATH_DENIED", "Path matches a deny rule.");
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            return ToolResult<string>.Fail("PATH_NOT_FOUND", "Path does not exist.");

        return ToolResult<string>.Ok(fullPath);
    }

    private static bool ContainsReparsePoint(string fullPath)
    {
        try
        {
            var current = File.Exists(fullPath) ? new FileInfo(fullPath).Directory : new DirectoryInfo(fullPath);
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                current = current.Parent;
            }
            if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch { return true; } // fail closed when link metadata cannot be verified
        return false;
    }

    private static bool IsUnderRoot(string candidate, string root, StringComparison comparison)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        candidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, comparison);
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
