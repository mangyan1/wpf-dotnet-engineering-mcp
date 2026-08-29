using System.Security.Cryptography;
using System.Text;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Wpf;

/// <summary>
/// Converts redacted UIA snapshots into metadata-only diagnostics. No element Name,
/// AutomationId, class name, framework name, text, or value crosses this boundary.
/// </summary>
public static class SafeUiAnalysis
{
    private static readonly HashSet<string> ActionableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "Edit", "CheckBox", "RadioButton", "ComboBox", "ListItem", "DataItem",
        "TreeItem", "MenuItem", "Hyperlink", "TabItem", "Slider"
    };

    private static readonly HashSet<string> ItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DataItem", "ListItem", "TreeItem"
    };

    public static SafeUiElementState ElementState(UiElementSnapshot element)
        => new(element.Reference, element.ControlType, element.IsEnabled, element.IsOffscreen,
            element.IsKeyboardFocusable, element.IsPassword,
            element.SupportedPatterns.OrderBy(value => value, StringComparer.Ordinal).Take(32).ToArray());

    public static SelectorAuditSummary SelectorAudit(UiSnapshot snapshot)
    {
        var actionable = snapshot.Elements
            .Where(element => ActionableTypes.Contains(element.ControlType) && !IsNativeProviderChrome(element))
            .ToArray();
        var duplicateIds = snapshot.Elements
            .Where(element => !string.IsNullOrWhiteSpace(element.AutomationId))
            .GroupBy(element => element.AutomationId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();
        var duplicateReferences = duplicateIds.SelectMany(group => group.Select(element => element.Reference)).ToHashSet(StringComparer.Ordinal);
        var findings = new List<SelectorAuditFinding>();

        foreach (var element in actionable)
        {
            if (string.IsNullOrWhiteSpace(element.AutomationId))
                findings.Add(new("medium", "missing-automation-id", element.Reference, element.ControlType));
            else if (duplicateReferences.Contains(element.Reference))
                findings.Add(new("high", "duplicate-automation-id", element.Reference, element.ControlType));

            if (string.IsNullOrWhiteSpace(element.AutomationId) && string.IsNullOrWhiteSpace(element.Name))
                findings.Add(new("high", "missing-semantic-identity", element.Reference, element.ControlType));
        }

        return new SelectorAuditSummary(
            snapshot.Elements.Count,
            actionable.Length,
            actionable.Count(element => !string.IsNullOrWhiteSpace(element.AutomationId) && !duplicateReferences.Contains(element.Reference)),
            actionable.Count(element => string.IsNullOrWhiteSpace(element.AutomationId)),
            duplicateIds.Length,
            findings.Take(250).ToArray(),
            snapshot.Truncated || findings.Count > 250);
    }

    private static bool IsNativeProviderChrome(UiElementSnapshot element)
        => element.ControlType.Equals("MenuItem", StringComparison.OrdinalIgnoreCase) &&
           element.Name.Equals("System", StringComparison.OrdinalIgnoreCase) &&
           string.IsNullOrWhiteSpace(element.AutomationId);

    public static DuplicateAutomationIdSummary DuplicateAutomationIds(UiSnapshot snapshot)
    {
        var groups = snapshot.Elements
            .Where(element => !string.IsNullOrWhiteSpace(element.AutomationId))
            .GroupBy(element => element.AutomationId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DuplicateAutomationIdGroup(
                Fingerprint(group.Key),
                group.Count(),
                group.Select(element => element.Reference).Take(50).ToArray()))
            .Take(100)
            .ToArray();
        return new DuplicateAutomationIdSummary(groups.Length, groups, snapshot.Truncated);
    }

    public static UiInventorySummary ControlInventory(UiSnapshot snapshot)
        => Inventory(snapshot, snapshot.Elements.Select(element => element.ControlType));

    public static UiInventorySummary PatternInventory(UiSnapshot snapshot)
        => Inventory(snapshot, snapshot.Elements.SelectMany(element => element.SupportedPatterns));

    public static GridMetadataSummary GridSummary(UiSnapshot snapshot)
    {
        var rows = snapshot.Elements.Where(element => element.ControlType.Equals("DataItem", StringComparison.OrdinalIgnoreCase)).ToArray();
        var headers = snapshot.Elements.Count(element => element.ControlType is "Header" or "HeaderItem");
        var cells = snapshot.Elements.Count(element => element.ControlType is "Custom" or "Edit" or "Text");
        return new(rows.Length, headers, cells, rows.Count(row => !row.IsOffscreen), snapshot.Truncated);
    }

    public static TreeMetadataSummary TreeSummary(UiSnapshot snapshot)
    {
        var nodes = snapshot.Elements.Where(element => element.ControlType.Equals("TreeItem", StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(
            nodes.Length,
            nodes.Count(node => !node.IsOffscreen),
            nodes.Count(node => node.SupportedPatterns.Any(pattern => pattern.Contains("ExpandCollapse", StringComparison.OrdinalIgnoreCase))),
            nodes.Length == 0 ? 0 : nodes.Max(node => node.Depth),
            snapshot.Truncated);
    }

    public static ItemsMetadataSummary ItemsSummary(UiSnapshot snapshot)
    {
        var items = snapshot.Elements.Where(element => ItemTypes.Contains(element.ControlType)).ToArray();
        return new(
            items.Length,
            items.Count(item => !item.IsOffscreen),
            items.Count(item => item.IsEnabled),
            items.Count(item => item.SupportedPatterns.Any(pattern => pattern.Contains("SelectionItem", StringComparison.OrdinalIgnoreCase))),
            snapshot.Truncated);
    }

    public static AccessibilityMetadataSummary AccessibilitySummary(UiSnapshot snapshot)
    {
        var interactive = snapshot.Elements.Where(element => ActionableTypes.Contains(element.ControlType)).ToArray();
        return new(
            interactive.Length,
            interactive.Count(element => string.IsNullOrWhiteSpace(element.Name) && string.IsNullOrWhiteSpace(element.AutomationId)),
            interactive.Count(element => element.IsEnabled && !element.IsOffscreen && !element.IsKeyboardFocusable),
            snapshot.Elements.Count(element => element.IsPassword),
            interactive.Count(element => element.IsOffscreen),
            snapshot.Truncated);
    }

    private static UiInventorySummary Inventory(UiSnapshot snapshot, IEnumerable<string> values)
    {
        var counts = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.Ordinal)
            .Select(group => new UiCountEntry(group.Key, group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        return new UiInventorySummary(snapshot.Elements.Count, counts, snapshot.Truncated);
    }

    private static string Fingerprint(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
