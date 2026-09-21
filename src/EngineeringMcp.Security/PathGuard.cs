namespace EngineeringMcp.Security;

/// <summary>
/// Shared path-containment and reparse-point primitives for FileGuard and the workspace policy
/// provisioner, so one containment fix propagates to both security surfaces.
/// </summary>
internal static class PathGuard
{
    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies beneath it.</summary>
    public static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
               (!Path.IsPathFullyQualified(relative) &&
                relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    /// <summary>True when <paramref name="path"/> lies strictly beneath <paramref name="root"/>.</summary>
    public static bool IsStrictlyWithin(string root, string path)
        => !string.Equals(root, path, StringComparison.OrdinalIgnoreCase) && IsWithin(root, path);

    /// <summary>
    /// Detects symbolic links, junctions, or other reparse points on the path or any of its parent
    /// directories. Returns true (fail closed) when link metadata cannot be verified.
    /// </summary>
    public static bool ContainsReparsePoint(string fullPath)
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

    /// <summary>
    /// Throws when <paramref name="path"/> leaves <paramref name="root"/> or any segment between
    /// them is a reparse point; used to keep authorization roots from escaping via links.
    /// </summary>
    public static void EnsureNoReparsePoints(string root, string path)
    {
        if (!IsWithin(root, path))
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
}
