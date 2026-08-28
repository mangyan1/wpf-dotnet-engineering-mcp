using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed partial class McpCompatibilityTests
{
    [TestMethod]
    public void PublishedToolNames_AreCodexCompatible()
    {
        var root = TestRepositoryLocator.FindRoot();
        var hostDirectory = Path.Combine(root, "src", "EngineeringMcp.Host");
        var names = Directory.EnumerateFiles(hostDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(path => ToolNameDeclarationRegex().Matches(File.ReadAllText(path)).Select(match => match.Groups["name"].Value))
            .ToArray();

        Assert.IsGreaterThan(0, names.Length, "No MCP tool declarations were found.");
        var invalid = names.Where(name => !CodexToolNameRegex().IsMatch(name)).ToArray();
        Assert.AreEqual(0, invalid.Length, "Codex accepts MCP tool names containing only lowercase letters, digits, underscores, and hyphens. Invalid: " + string.Join(", ", invalid));
    }

    [GeneratedRegex("McpServerTool\\(Name\\s*=\\s*\\\"(?<name>[^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNameDeclarationRegex();

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodexToolNameRegex();
}
