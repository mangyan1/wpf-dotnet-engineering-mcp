namespace EngineeringMcp.Security;

/// <summary>
/// Single source of truth for sensitive-file deny patterns, shared by FileGuard (read-time
/// enforcement) and WpfWorkspacePolicyProvisioner (written denyGlobs). Sharing the list keeps a
/// hand-written policy without denyGlobs from reading credential, key, dump, or database files.
/// </summary>
internal static class SensitiveFileRules
{
    public static readonly string[] DenyGlobs =
    [
        "**/.env",
        "**/.env.*",
        "**/secrets.json",
        "**/appsettings.Production.json",
        "**/*.pfx",
        "**/*.p12",
        "**/*.pem",
        "**/*.key",
        "**/*.snk",
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
}
