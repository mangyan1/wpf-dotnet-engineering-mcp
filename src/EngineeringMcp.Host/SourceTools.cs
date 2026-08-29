using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Source;
using ModelContextProtocol.Server;
using ModelContextProtocol;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class SourceTools
{
    [McpServerTool(Name = "source_inventory", UseStructuredContent = true), Description("Inventories only files beneath an approved source root and obeys deny globs.")]
    public static ToolResult<SourceProjectInventory> Inventory(
        [Description("Path beneath a source root explicitly allowed by policy.")] string root,
        SourceIntelligenceService source,
        ToolAuthorization auth)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_inventory").ToPolicy(), root, () => source.Inventory(root));

    [McpServerTool(Name = "source_read", UseStructuredContent = true), Description("Reads a bounded line range from an approved source file. Content is redacted before MCP output.")]
    public static ToolResult<SourceReadResult> Read(
        [Description("File path beneath a source root explicitly allowed by policy.")] string path,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("One-based first source line to return.")] int startLine = 1,
        [Description("Maximum number of source lines to return; the server applies a hard upper bound.")] int maxLines = 400)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_read").ToPolicy(), path, () => source.Read(path, startLine, maxLines));

    [McpServerTool(Name = "source_find_symbol", UseStructuredContent = true), Description("Finds C# declarations syntactically under an approved source root; results include file/line evidence.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindSymbol(
        [Description("Path beneath a source root explicitly allowed by policy.")] string root,
        [Description("Exact C# identifier to locate in approved source.")] string symbolName,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Maximum number of results to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_find_symbol").ToPolicy(), root, () => source.FindSymbol(root, symbolName, maxResults));

    [McpServerTool(Name = "source_find_references", UseStructuredContent = true), Description("Finds bounded syntactic identifier references under an approved source root. It does not claim full semantic-reference resolution.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindReferences(
        [Description("Path beneath a source root explicitly allowed by policy.")] string root,
        [Description("Exact C# identifier to locate in approved source.")] string identifier,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Maximum number of results to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_find_references").ToPolicy(), root, () => source.FindReferences(root, identifier, maxResults));

    [McpServerTool(Name = "source_find_references_page", UseStructuredContent = true), Description("Returns one deterministic bounded page of syntactic C# identifier references beneath an approved source root, with an explicit next offset when more results exist.")]
    public static ToolResult<PagedResult<SourceLocation>> FindReferencesPage(
        [Description("Path beneath a source root explicitly allowed by policy.")] string root,
        [Description("Exact C# identifier to locate in approved source.")] string identifier,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Zero-based result offset for deterministic bounded pagination.")] int offset = 0,
        [Description("Number of results requested for one page; the server applies a hard upper bound.")] int pageSize = 100)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_find_references_page").ToPolicy(), root, () =>
        {
            offset = Math.Clamp(offset, 0, 1_999);
            pageSize = Math.Clamp(pageSize, 1, 200);
            var requested = Math.Min(2_000, offset + pageSize + 1);
            var found = source.FindReferences(root, identifier, requested);
            if (!found.Success || found.Value is null)
                return ToolResult<PagedResult<SourceLocation>>.Fail(found.Error!.Code, found.Error.Message, found.Error.Retryable);
            var items = found.Value.Skip(offset).Take(pageSize).ToArray();
            var hasMore = found.Value.Count > offset + items.Length;
            return ToolResult<PagedResult<SourceLocation>>.Ok(new PagedResult<SourceLocation>(
                items, offset, pageSize, hasMore ? offset + items.Length : null, hasMore));
        });

    [McpServerTool(Name = "source_find_references_semantic", UseStructuredContent = true), Description("Uses an approved MSBuild solution/project and Roslyn semantic symbols to find bounded C# references. Fails explicitly when a compilable project model cannot be loaded.")]
    public static Task<ToolResult<IReadOnlyList<SourceLocation>>> FindSemanticReferences(
        [Description("Path beneath a source root explicitly allowed by policy.")] string root,
        [Description("Exact C# identifier to locate in approved source.")] string symbolName,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        IProgress<ProgressNotificationValue> progress,
        [Description("Maximum number of results to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Async(auth, ToolPolicyCatalog.Get("source_find_references_semantic").ToPolicy(), root, async () =>
        {
            progress.Report(new ProgressNotificationValue { Progress = 0, Total = 100, Message = "Loading the approved MSBuild project model." });
            var result = await source.FindSemanticReferencesAsync(root, symbolName, maxResults, cancellationToken).ConfigureAwait(false);
            progress.Report(new ProgressNotificationValue { Progress = 100, Total = 100, Message = "Semantic reference search completed." });
            return result;
        });

    [McpServerTool(Name = "source_analyze_xaml", UseStructuredContent = true), Description("Audits one approved XAML file or all XAML beneath an approved directory for measurable issues including hard-coded colors, sensitive-looking attributes, and missing automation metadata.")]
    public static ToolResult<IReadOnlyList<XamlFinding>> AnalyzeXaml(
        [Description("Approved .xaml file path or directory beneath a source root explicitly allowed by policy.")] string root,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Maximum number of findings to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_analyze_xaml").ToPolicy(), root, () => source.AnalyzeXaml(root, maxResults));

    [McpServerTool(Name = "wpfui_audit_resources", UseStructuredContent = true), Description("Static WPF/WPF-UI resource guard for one approved XAML file or directory: reports measurable hard-coded brush/color usage. It does not invent a project-specific token catalogue.")]
    public static ToolResult<IReadOnlyList<XamlFinding>> AuditWpfUiResources(
        [Description("Approved .xaml file path or directory beneath a source root explicitly allowed by policy.")] string root,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Maximum number of findings to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("wpfui_audit_resources").ToPolicy(), root, () =>
        {
            var analyzed = source.AnalyzeXaml(root, Math.Clamp(maxResults * 4, 1, 5_000));
            if (!analyzed.Success || analyzed.Value is null)
                return ToolResult<IReadOnlyList<XamlFinding>>.Fail(analyzed.Error!.Code, analyzed.Error.Message, analyzed.Error.Retryable);
            return ToolResult<IReadOnlyList<XamlFinding>>.Ok(analyzed.Value
                .Where(x => string.Equals(x.Rule, "WPF001_HARDCODED_COLOR", StringComparison.Ordinal))
                .Take(Math.Clamp(maxResults, 1, 1_000))
                .ToArray());
        });

    [McpServerTool(Name = "source_find_automation_id", UseStructuredContent = true), Description("Maps an AutomationId to approved XAML source locations.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindAutomationId(
        [Description("Approved .xaml file path or directory beneath a source root explicitly allowed by policy.")] string root,
        [Description("Exact WPF AutomationId previously observed on a UI element.")] string automationId,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Maximum number of results to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_find_automation_id").ToPolicy(), root, () => source.FindAutomationId(root, automationId, maxResults));

    [McpServerTool(Name = "source_find_binding", UseStructuredContent = true), Description("Finds exact WPF Binding Path evidence in approved XAML without guessing from visually similar names.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindBinding(
        [Description("Approved .xaml file path or directory beneath a source root explicitly allowed by policy.")] string root,
        [Description("Exact WPF Binding Path to locate in approved XAML.")] string bindingPath,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Maximum number of results to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_find_binding").ToPolicy(), root, () => source.FindBinding(root, bindingPath, maxResults));

    [McpServerTool(Name = "source_map_stacktrace", UseStructuredContent = true), Description("Maps file/line locations already present in a stack trace to approved source paths. It does not fabricate missing symbols.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> MapStackTrace(
        [Description("Redacted stack trace containing source file and line evidence to map.")] string stackTrace,
        SourceIntelligenceService source,
        ToolAuthorization auth,
        [Description("Maximum number of results to return; the server applies a hard upper bound.")] int maxResults = 200)
        => ToolRun.Sync(auth, ToolPolicyCatalog.Get("source_map_stacktrace").ToPolicy(), "stacktrace", () => source.MapStackTrace(stackTrace, maxResults));
}
