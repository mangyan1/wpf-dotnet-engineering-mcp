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
    public static ToolResult<SourceProjectInventory> Inventory(string root, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_inventory", "source.roslyn", root, () => source.Inventory(root), auth);

    [McpServerTool(Name = "source_read", UseStructuredContent = true), Description("Reads a bounded line range from an approved source file. Content is redacted before MCP output.")]
    public static ToolResult<SourceReadResult> Read(string path, int startLine, int maxLines, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_read", "source.roslyn", path, () => source.Read(path, startLine, maxLines), auth);

    [McpServerTool(Name = "source_find_symbol", UseStructuredContent = true), Description("Finds C# declarations syntactically under an approved source root; results include file/line evidence.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindSymbol(string root, string symbolName, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_find_symbol", "source.roslyn", root, () => source.FindSymbol(root, symbolName, maxResults), auth);

    [McpServerTool(Name = "source_find_references", UseStructuredContent = true), Description("Finds bounded syntactic identifier references under an approved source root. It does not claim full semantic-reference resolution.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindReferences(string root, string identifier, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_find_references", "source.roslyn", root, () => source.FindReferences(root, identifier, maxResults), auth);

    [McpServerTool(Name = "source_find_references_page", UseStructuredContent = true), Description("Returns one deterministic bounded page of syntactic C# identifier references beneath an approved source root, with an explicit next offset when more results exist.")]
    public static ToolResult<PagedResult<SourceLocation>> FindReferencesPage(string root, string identifier, int offset, int pageSize, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_find_references_page", "source.roslyn", root, () =>
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
        }, auth);

    [McpServerTool(Name = "source_find_references_semantic", UseStructuredContent = true), Description("Uses an approved MSBuild solution/project and Roslyn semantic symbols to find bounded C# references. Fails explicitly when a compilable project model cannot be loaded.")]
    public static async Task<ToolResult<IReadOnlyList<SourceLocation>>> FindSemanticReferences(string root, string symbolName, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth, CancellationToken cancellationToken, IProgress<ProgressNotificationValue> progress)
    {
        progress.Report(new ProgressNotificationValue { Progress = 0, Total = 100, Message = "Loading the approved MSBuild project model." });
        var policy = new ToolPolicy("source_find_references_semantic", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "source.roslyn");
        var allowed = auth.Authorize(policy, root);
        if (!allowed.Success) return ToolResult<IReadOnlyList<SourceLocation>>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await source.FindSemanticReferencesAsync(root, symbolName, maxResults, cancellationToken).ConfigureAwait(false);
        progress.Report(new ProgressNotificationValue { Progress = 100, Total = 100, Message = "Semantic reference search completed." });
        auth.Complete(allowed.Value!, policy, root, result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }

    [McpServerTool(Name = "source_analyze_xaml", UseStructuredContent = true), Description("Audits approved XAML for measurable issues including hard-coded colors, sensitive-looking attributes, and missing automation metadata.")]
    public static ToolResult<IReadOnlyList<XamlFinding>> AnalyzeXaml(string root, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_analyze_xaml", "source.xaml", root, () => source.AnalyzeXaml(root, maxResults), auth);

    [McpServerTool(Name = "wpfui_audit_resources", UseStructuredContent = true), Description("Static WPF/WPF-UI resource guard: reports measurable hard-coded brush/color usage in approved XAML. It does not invent a project-specific token catalogue.")]
    public static ToolResult<IReadOnlyList<XamlFinding>> AuditWpfUiResources(string root, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("wpfui_audit_resources", "wpfui.static_audit", root, () =>
        {
            var analyzed = source.AnalyzeXaml(root, Math.Clamp(maxResults * 4, 1, 5_000));
            if (!analyzed.Success || analyzed.Value is null)
                return ToolResult<IReadOnlyList<XamlFinding>>.Fail(analyzed.Error!.Code, analyzed.Error.Message, analyzed.Error.Retryable);
            return ToolResult<IReadOnlyList<XamlFinding>>.Ok(analyzed.Value
                .Where(x => string.Equals(x.Rule, "WPF001_HARDCODED_COLOR", StringComparison.Ordinal))
                .Take(Math.Clamp(maxResults, 1, 1_000))
                .ToArray());
        }, auth);

    [McpServerTool(Name = "source_find_automation_id", UseStructuredContent = true), Description("Maps an AutomationId to approved XAML source locations.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindAutomationId(string root, string automationId, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_find_automation_id", "source.xaml", root, () => source.FindAutomationId(root, automationId, maxResults), auth);

    [McpServerTool(Name = "source_find_binding", UseStructuredContent = true), Description("Finds exact WPF Binding Path evidence in approved XAML without guessing from visually similar names.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> FindBinding(string root, string bindingPath, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_find_binding", "source.xaml", root, () => source.FindBinding(root, bindingPath, maxResults), auth);

    [McpServerTool(Name = "source_map_stacktrace", UseStructuredContent = true), Description("Maps file/line locations already present in a stack trace to approved source paths. It does not fabricate missing symbols.")]
    public static ToolResult<IReadOnlyList<SourceLocation>> MapStackTrace(string stackTrace, int maxResults, ISourceIntelligenceService source, IToolAuthorization auth)
        => Run("source_map_stacktrace", "source.roslyn", "stacktrace", () => source.MapStackTrace(stackTrace, maxResults), auth);

    private static ToolResult<T> Run<T>(string tool, string capability, string target, Func<ToolResult<T>> action, IToolAuthorization auth)
    {
        var policy = new ToolPolicy(tool, PermissionLevel.ApplicationDiagnostics, RiskClass.Read, capability);
        var allowed = auth.Authorize(policy, target);
        if (!allowed.Success) return ToolResult<T>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = action();
        auth.Complete(allowed.Value!, policy, target, result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }
}
