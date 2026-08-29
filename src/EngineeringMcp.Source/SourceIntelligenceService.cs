using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;

namespace EngineeringMcp.Source;

public sealed record SourceProjectInventory(
    string Root,
    IReadOnlyList<string> Solutions,
    IReadOnlyList<string> Projects,
    int CSharpFiles,
    int XamlFiles,
    bool Truncated);

public sealed partial class SourceIntelligenceService(
    FileGuard fileGuard,
    FilePolicyProvider policyProvider,
    RedactionService redactor)
{
    private const int MaxFilesToScan = 10_000;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    public ToolResult<SourceProjectInventory> Inventory(string root)
    {
        var allowed = fileGuard.RequireReadable(root);
        if (!allowed.Success || allowed.Value is null)
            return ToolResult<SourceProjectInventory>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        if (!Directory.Exists(allowed.Value))
            return ToolResult<SourceProjectInventory>.Fail("DIRECTORY_REQUIRED", "Inventory requires an approved directory.");

        try
        {
            var files = EnumerateApprovedFiles(allowed.Value, ["*.sln", "*.slnx", "*.csproj", "*.cs", "*.xaml"], MaxFilesToScan + 1).ToArray();
            var truncated = files.Length > MaxFilesToScan;
            files = files.Take(MaxFilesToScan).ToArray();
            return ToolResult<SourceProjectInventory>.Ok(new SourceProjectInventory(
                allowed.Value,
                files.Where(IsSolution).ToArray(),
                files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray(),
                files.Count(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)),
                files.Count(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)),
                truncated));
        }
        catch (Exception ex) { return ToolResult<SourceProjectInventory>.Fail("SOURCE_INVENTORY_FAILED", Safe(ex.Message)); }
    }

    public ToolResult<SourceReadResult> Read(string path, int startLine = 1, int maxLines = 200)
    {
        var allowed = fileGuard.RequireReadable(path);
        if (!allowed.Success || allowed.Value is null)
            return ToolResult<SourceReadResult>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        if (!File.Exists(allowed.Value)) return ToolResult<SourceReadResult>.Fail("FILE_REQUIRED", "Source read requires a file path.");
        var info = new FileInfo(allowed.Value);
        if (info.Length > MaxFileBytes) return ToolResult<SourceReadResult>.Fail("FILE_TOO_LARGE", "Source file exceeds the 4 MiB read limit.");

        startLine = Math.Max(1, startLine);
        maxLines = Math.Clamp(maxLines, 1, 1_000);
        try
        {
            var lines = File.ReadLines(allowed.Value).Skip(startLine - 1).Take(maxLines + 1).ToArray();
            var truncated = lines.Length > maxLines;
            var content = string.Join(Environment.NewLine, lines.Take(maxLines));
            content = redactor.Redact(content, policyProvider.Current.Pii);
            return ToolResult<SourceReadResult>.Ok(new SourceReadResult(allowed.Value, startLine, startLine + Math.Min(lines.Length, maxLines) - 1, content, truncated));
        }
        catch (Exception ex) { return ToolResult<SourceReadResult>.Fail("SOURCE_READ_FAILED", Safe(ex.Message)); }
    }

    public ToolResult<IReadOnlyList<SourceLocation>> FindSymbol(string root, string symbolName, int maxResults = 100)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return ToolResult<IReadOnlyList<SourceLocation>>.Fail("SYMBOL_REQUIRED", "Symbol name is required.");
        var rootResult = RequireDirectory(root);
        if (!rootResult.Success || rootResult.Value is null) return ToolResult<IReadOnlyList<SourceLocation>>.Fail(rootResult.Error!.Code, rootResult.Error.Message);
        maxResults = Math.Clamp(maxResults, 1, 1_000);
        var results = new List<SourceLocation>();
        try
        {
            foreach (var file in EnumerateApprovedFiles(rootResult.Value, ["*.cs"], MaxFilesToScan))
            {
                if (results.Count >= maxResults) break;
                var text = ReadSmallFile(file); if (text is null) continue;
                var rootNode = CSharpSyntaxTree.ParseText(text).GetRoot();
                IEnumerable<SyntaxNode> declarations = rootNode.DescendantNodes().Where(n => n switch
                {
                    BaseTypeDeclarationSyntax t => t.Identifier.ValueText == symbolName,
                    MethodDeclarationSyntax m => m.Identifier.ValueText == symbolName,
                    PropertyDeclarationSyntax p => p.Identifier.ValueText == symbolName,
                    EventDeclarationSyntax e => e.Identifier.ValueText == symbolName,
                    VariableDeclaratorSyntax v => v.Identifier.ValueText == symbolName,
                    _ => false
                });
                foreach (var node in declarations)
                {
                    results.Add(ToLocation(file, node, node.Kind().ToString(), symbolName, FindContainer(node)));
                    if (results.Count >= maxResults) break;
                }
            }
            return ToolResult<IReadOnlyList<SourceLocation>>.Ok(results);
        }
        catch (Exception ex) { return ToolResult<IReadOnlyList<SourceLocation>>.Fail("SYMBOL_SEARCH_FAILED", Safe(ex.Message)); }
    }

    public ToolResult<IReadOnlyList<SourceLocation>> FindReferences(string root, string identifier, int maxResults = 200)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return ToolResult<IReadOnlyList<SourceLocation>>.Fail("IDENTIFIER_REQUIRED", "Identifier is required.");
        var rootResult = RequireDirectory(root);
        if (!rootResult.Success || rootResult.Value is null) return ToolResult<IReadOnlyList<SourceLocation>>.Fail(rootResult.Error!.Code, rootResult.Error.Message);
        maxResults = Math.Clamp(maxResults, 1, 2_000);
        var results = new List<SourceLocation>();
        try
        {
            foreach (var file in EnumerateApprovedFiles(rootResult.Value, ["*.cs"], MaxFilesToScan))
            {
                if (results.Count >= maxResults) break;
                var text = ReadSmallFile(file); if (text is null || !text.Contains(identifier, StringComparison.Ordinal)) continue;
                var syntaxRoot = CSharpSyntaxTree.ParseText(text).GetRoot();
                foreach (var node in syntaxRoot.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.ValueText == identifier))
                {
                    results.Add(ToLocation(file, node, "IdentifierReference", identifier, FindContainer(node)));
                    if (results.Count >= maxResults) break;
                }
            }
            return ToolResult<IReadOnlyList<SourceLocation>>.Ok(results);
        }
        catch (Exception ex) { return ToolResult<IReadOnlyList<SourceLocation>>.Fail("REFERENCE_SEARCH_FAILED", Safe(ex.Message)); }
    }

    public async Task<ToolResult<IReadOnlyList<SourceLocation>>> FindSemanticReferencesAsync(
        string root,
        string symbolName,
        int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return ToolResult<IReadOnlyList<SourceLocation>>.Fail("SYMBOL_REQUIRED", "Symbol name is required.");
        var rootResult = RequireDirectory(root);
        if (!rootResult.Success || rootResult.Value is null)
            return ToolResult<IReadOnlyList<SourceLocation>>.Fail(rootResult.Error!.Code, rootResult.Error.Message);
        maxResults = Math.Clamp(maxResults, 1, 2_000);

        try
        {
            EnsureMsBuildRegistered();
            using var workspace = MSBuildWorkspace.Create();
            var solutionPath = EnumerateApprovedFiles(rootResult.Value, ["*.sln", "*.slnx"], 2).FirstOrDefault();
            Solution solution;
            if (solutionPath is not null)
            {
                solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var projectPath = EnumerateApprovedFiles(rootResult.Value, ["*.csproj"], 2).FirstOrDefault();
                if (projectPath is null)
                    return ToolResult<IReadOnlyList<SourceLocation>>.Fail("PROJECT_NOT_FOUND", "No approved solution or C# project was found beneath the source root.");
                var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                solution = project.Solution;
            }

            var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                solution, symbolName, ignoreCase: false, SymbolFilter.All, cancellationToken).ConfigureAwait(false);
            var results = new List<SourceLocation>();
            foreach (var symbol in symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
                foreach (var location in references.SelectMany(reference => reference.Locations))
                {
                    if (!location.Location.IsInSource) continue;
                    var span = location.Location.GetLineSpan();
                    if (string.IsNullOrWhiteSpace(span.Path) || !IsWithinRoot(span.Path, rootResult.Value)) continue;
                    results.Add(new SourceLocation(
                        span.Path,
                        span.StartLinePosition.Line + 1,
                        span.StartLinePosition.Character + 1,
                        "SemanticReference",
                        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    if (results.Count >= maxResults)
                        return ToolResult<IReadOnlyList<SourceLocation>>.Ok(results);
                }
            }
            return ToolResult<IReadOnlyList<SourceLocation>>.Ok(results);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolResult<IReadOnlyList<SourceLocation>>.Fail("SEMANTIC_REFERENCE_SEARCH_FAILED", Safe(ex.Message), true);
        }
    }

    private static readonly object MsBuildRegistrationLock = new();

    private static void EnsureMsBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered) return;
        lock (MsBuildRegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        }
    }

    public ToolResult<IReadOnlyList<XamlFinding>> AnalyzeXaml(string path, int maxResults = 500)
    {
        var targets = ResolveXamlTargets(path);
        if (!targets.Success || targets.Value is null) return ToolResult<IReadOnlyList<XamlFinding>>.Fail(targets.Error!.Code, targets.Error.Message);
        maxResults = Math.Clamp(maxResults, 1, 5_000);
        var findings = new List<XamlFinding>();
        try
        {
            foreach (var file in targets.Value)
            {
                if (findings.Count >= maxResults) break;
                var text = ReadSmallFile(file); if (text is null) continue;
                XDocument doc;
                try { doc = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace); }
                catch (XmlException ex)
                {
                    findings.Add(new XamlFinding(file, ex.LineNumber, "high", "XAML_PARSE", "XAML is not well-formed.", Safe(ex.Message)));
                    continue;
                }

                foreach (var element in doc.Descendants())
                {
                    foreach (var attr in element.Attributes())
                    {
                        var line = ((IXmlLineInfo)attr).HasLineInfo() ? ((IXmlLineInfo)attr).LineNumber : 1;
                        if (IsBrushProperty(attr.Name.LocalName) && HexColorRegex().IsMatch(attr.Value) && !attr.Value.Contains("Resource", StringComparison.OrdinalIgnoreCase))
                            findings.Add(new XamlFinding(file, line, "high", "WPF001_HARDCODED_COLOR", "Brush/color property contains a hard-coded color instead of a resource token.", $"{attr.Name.LocalName}={Safe(attr.Value)}"));

                        if (attr.Name.LocalName is "Password" or "AccessToken" or "ApiKey" or "ClientSecret")
                            findings.Add(new XamlFinding(file, line, "critical", "SEC001_SECRET_IN_XAML", "Potential secret-bearing property must not be populated in XAML.", attr.Name.LocalName));
                    }

                    var local = element.Name.LocalName;
                    var actionable = local is "Button" or "Hyperlink" or "CheckBox" or "RadioButton" or "ComboBox" or "MenuItem";
                    if (actionable)
                    {
                        var automationId = element.Attributes().FirstOrDefault(a => XamlAttributeNameMatches(a.Name.LocalName, "AutomationId"))?.Value;
                        var name = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;
                        var content = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Content")?.Value;
                        if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(content))
                        {
                            var line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 1;
                            findings.Add(new XamlFinding(file, line, "medium", "A11Y001_UNNAMED_ACTION", "Interactive XAML element has no AutomationId, x:Name, or static Content evidence.", local));
                        }
                    }
                    if (findings.Count >= maxResults) break;
                }
            }
            return ToolResult<IReadOnlyList<XamlFinding>>.Ok(findings.Take(maxResults).ToArray());
        }
        catch (Exception ex) { return ToolResult<IReadOnlyList<XamlFinding>>.Fail("XAML_ANALYSIS_FAILED", Safe(ex.Message)); }
    }

    public ToolResult<IReadOnlyList<SourceLocation>> FindAutomationId(string root, string automationId, int maxResults = 50)
        => FindXamlAttribute(root, "AutomationId", automationId, "XamlAutomationId", maxResults);

    public ToolResult<IReadOnlyList<SourceLocation>> FindBinding(string path, string bindingPath, int maxResults = 100)
    {
        var targets = ResolveXamlTargets(path);
        if (!targets.Success || targets.Value is null) return ToolResult<IReadOnlyList<SourceLocation>>.Fail(targets.Error!.Code, targets.Error.Message);
        maxResults = Math.Clamp(maxResults, 1, 1_000);
        var results = new List<SourceLocation>();
        foreach (var file in targets.Value)
        {
            if (results.Count >= maxResults) break;
            var text = ReadSmallFile(file); if (text is null || !text.Contains(bindingPath, StringComparison.Ordinal)) continue;
            XDocument doc;
            try { doc = XDocument.Parse(text, LoadOptions.SetLineInfo); } catch { continue; }
            foreach (var attr in doc.Descendants().Attributes())
            {
                if (!attr.Value.Contains("{Binding", StringComparison.Ordinal) || !BindingPathMatches(attr.Value, bindingPath)) continue;
                var info = (IXmlLineInfo)attr;
                results.Add(new SourceLocation(file, info.HasLineInfo() ? info.LineNumber : 1, info.HasLineInfo() ? info.LinePosition : 1, "XamlBinding", bindingPath, attr.Parent?.Name.LocalName));
                if (results.Count >= maxResults) break;
            }
        }
        return ToolResult<IReadOnlyList<SourceLocation>>.Ok(results);
    }

    public ToolResult<IReadOnlyList<SourceLocation>> MapStackTrace(string stackTrace, int maxResults = 100)
        => MapStackTraceCore(stackTrace, sourceRoot: null, maxResults);

    public ToolResult<IReadOnlyList<SourceLocation>> MapStackTrace(string stackTrace, string sourceRoot, int maxResults = 100)
    {
        var rootResult = RequireDirectory(sourceRoot);
        if (!rootResult.Success || rootResult.Value is null)
            return ToolResult<IReadOnlyList<SourceLocation>>.Fail(rootResult.Error!.Code, rootResult.Error.Message);

        return MapStackTraceCore(stackTrace, rootResult.Value, maxResults);
    }

    private ToolResult<IReadOnlyList<SourceLocation>> MapStackTraceCore(string stackTrace, string? sourceRoot, int maxResults)
    {
        maxResults = Math.Clamp(maxResults, 1, 500);
        var results = new List<SourceLocation>();
        foreach (Match match in StackFileRegex().Matches(stackTrace ?? string.Empty))
        {
            if (results.Count >= maxResults) break;
            var path = match.Groups["file"].Value;
            var permitted = fileGuard.RequireReadable(path);
            if (!permitted.Success || permitted.Value is null || !File.Exists(permitted.Value)) continue;
            if (sourceRoot is not null && !IsWithinRoot(permitted.Value, sourceRoot)) continue;
            _ = int.TryParse(match.Groups["line"].Value, out var line);
            results.Add(new SourceLocation(permitted.Value, Math.Max(1, line), 1, "StackTrace", Path.GetFileName(permitted.Value)));
        }
        return ToolResult<IReadOnlyList<SourceLocation>>.Ok(results);
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, comparison);
    }

    private ToolResult<IReadOnlyList<SourceLocation>> FindXamlAttribute(string path, string attributeLocalName, string value, string kind, int maxResults)
    {
        var targets = ResolveXamlTargets(path);
        if (!targets.Success || targets.Value is null) return ToolResult<IReadOnlyList<SourceLocation>>.Fail(targets.Error!.Code, targets.Error.Message);
        var results = new List<SourceLocation>();
        foreach (var file in targets.Value)
        {
            if (results.Count >= maxResults) break;
            var text = ReadSmallFile(file); if (text is null || !text.Contains(value, StringComparison.Ordinal)) continue;
            XDocument doc; try { doc = XDocument.Parse(text, LoadOptions.SetLineInfo); } catch { continue; }
            foreach (var attr in doc.Descendants().Attributes().Where(a => XamlAttributeNameMatches(a.Name.LocalName, attributeLocalName) && a.Value == value))
            {
                var info = (IXmlLineInfo)attr;
                results.Add(new SourceLocation(file, info.HasLineInfo() ? info.LineNumber : 1, info.HasLineInfo() ? info.LinePosition : 1, kind, value, attr.Parent?.Name.LocalName));
                if (results.Count >= maxResults) break;
            }
        }
        return ToolResult<IReadOnlyList<SourceLocation>>.Ok(results);
    }

    private ToolResult<IReadOnlyList<string>> ResolveXamlTargets(string path)
    {
        var allowed = fileGuard.RequireReadable(path);
        if (!allowed.Success || allowed.Value is null)
            return ToolResult<IReadOnlyList<string>>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);

        if (File.Exists(allowed.Value))
        {
            if (!string.Equals(Path.GetExtension(allowed.Value), ".xaml", StringComparison.OrdinalIgnoreCase))
                return ToolResult<IReadOnlyList<string>>.Fail("XAML_FILE_REQUIRED", "An approved .xaml file or directory is required.");
            return ToolResult<IReadOnlyList<string>>.Ok([allowed.Value]);
        }

        if (Directory.Exists(allowed.Value))
            return ToolResult<IReadOnlyList<string>>.Ok(EnumerateApprovedFiles(allowed.Value, ["*.xaml"], MaxFilesToScan).ToArray());

        return ToolResult<IReadOnlyList<string>>.Fail("PATH_NOT_FOUND", "The approved XAML path does not exist.");
    }

    private ToolResult<string> RequireDirectory(string root)
    {
        var allowed = fileGuard.RequireReadable(root);
        if (!allowed.Success || allowed.Value is null) return ToolResult<string>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        return Directory.Exists(allowed.Value) ? ToolResult<string>.Ok(allowed.Value) : ToolResult<string>.Fail("DIRECTORY_REQUIRED", "An approved directory is required.");
    }

    private IEnumerable<string> EnumerateApprovedFiles(string root, string[] patterns, int limit)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && seen.Count < limit)
        {
            var directory = pending.Pop();
            ToolResult<string> approvedDirectory;
            try { approvedDirectory = fileGuard.RequireReadable(directory); }
            catch { continue; }
            if (!approvedDirectory.Success || approvedDirectory.Value is null) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(approvedDirectory.Value, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { continue; }

            foreach (var path in files)
            {
                if (seen.Count >= limit) yield break;
                if (!patterns.Any(pattern => FileNameMatchesPattern(Path.GetFileName(path), pattern))) continue;
                if (!seen.Add(path)) continue;
                var allowed = fileGuard.RequireReadable(path);
                if (!allowed.Success || allowed.Value is null) continue;
                yield return allowed.Value;
            }

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(approvedDirectory.Value, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { continue; }
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(child);
                }
                catch { /* fail closed: inaccessible/unverifiable directory is skipped */ }
            }
        }
    }

    private static bool FileNameMatchesPattern(string fileName, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return fileName.EndsWith(pattern[1..], OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return string.Equals(fileName, pattern, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private string? ReadSmallFile(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Length <= MaxFileBytes ? File.ReadAllText(file) : null;
        }
        catch { return null; }
    }

    private static SourceLocation ToLocation(string file, SyntaxNode node, string kind, string name, string? container)
    {
        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        return new SourceLocation(file, span.Line + 1, span.Character + 1, kind, name, container);
    }

    private static string? FindContainer(SyntaxNode node)
        => node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

    private static bool IsSolution(string f) => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    private static bool IsBrushProperty(string name) => name is "Background" or "Foreground" or "BorderBrush" or "Fill" or "Stroke" or "Color";

    private static bool XamlAttributeNameMatches(string localName, string expectedName)
        => string.Equals(localName, expectedName, StringComparison.Ordinal)
           || localName.EndsWith("." + expectedName, StringComparison.Ordinal);

    private static bool BindingPathMatches(string expression, string target)
    {
        if (string.IsNullOrWhiteSpace(expression) || string.IsNullOrWhiteSpace(target)) return false;
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("{Binding", StringComparison.Ordinal) && !trimmed.StartsWith("{x:Bind", StringComparison.Ordinal)) return false;
        var body = trimmed.Trim('{', '}').Trim();
        var firstSpace = body.IndexOf(' ');
        if (firstSpace < 0) return false;
        body = body[(firstSpace + 1)..].Trim();
        foreach (var segment in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = segment;
            var equals = segment.IndexOf('=');
            if (equals >= 0)
            {
                var key = segment[..equals].Trim();
                if (!key.Equals("Path", StringComparison.OrdinalIgnoreCase)) continue;
                value = segment[(equals + 1)..].Trim();
            }
            return string.Equals(value, target, StringComparison.Ordinal);
        }
        return false;
    }

    private string Safe(string value) => redactor.Redact(value, policyProvider.Current.Pii);

    [GeneratedRegex(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();


    [GeneratedRegex(@"\sin\s(?<file>.+?):line\s(?<line>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StackFileRegex();
}
