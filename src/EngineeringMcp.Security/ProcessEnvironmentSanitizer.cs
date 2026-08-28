namespace EngineeringMcp.Security;

public static class ProcessEnvironmentSanitizer
{
    public static string SanitizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var safe = new List<string>();

        foreach (var rawEntry in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = Environment.ExpandEnvironmentVariables(rawEntry.Trim().Trim('"'));
            if (!IsSafeLocalAbsolutePath(entry))
                continue;

            string fullPath;
            try { fullPath = Path.GetFullPath(entry); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(fullPath))
                safe.Add(fullPath);
        }

        return string.Join(Path.PathSeparator, safe);
    }

    public static bool IsSafeLocalAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim().Trim('"');
        if (trimmed.StartsWith("\\\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;

        return Path.IsPathFullyQualified(trimmed);
    }

    public static void SanitizePathInPlace(IDictionary<string, string?> environment)
    {
        if (environment.TryGetValue("PATH", out var upperPath))
            environment["PATH"] = SanitizePath(upperPath);
        else if (environment.TryGetValue("Path", out var path))
            environment["Path"] = SanitizePath(path);
    }
}
