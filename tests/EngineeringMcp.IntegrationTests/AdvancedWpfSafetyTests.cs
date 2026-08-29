using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Wpf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed class AdvancedWpfSafetyTests
{
    [TestMethod]
    public void SafeUiAnalysis_NeverReturnsElementTextOrRawAutomationIds()
    {
        var snapshot = new UiSnapshot(
            42,
            "uia:42:1",
            DateTimeOffset.UtcNow,
            [
                Element("uia:42:1", null, "Window", "Synthetic Person", "RootWindow", ["Window"]),
                Element("uia:42:2", "uia:42:1", "Button", "user@example.invalid", "", ["Invoke"]),
                Element("uia:42:3", "uia:42:1", "DataItem", "Synthetic account 1001", "RowIdentifier", ["SelectionItem"]),
                Element("uia:42:4", "uia:42:1", "DataItem", "Synthetic account 1002", "RowIdentifier", ["SelectionItem"])
            ],
            false,
            100);

        var selectorAudit = SafeUiAnalysis.SelectorAudit(snapshot);
        var duplicates = SafeUiAnalysis.DuplicateAutomationIds(snapshot);
        var inventory = SafeUiAnalysis.ControlInventory(snapshot);
        var json = JsonSerializer.Serialize(new { selectorAudit, duplicates, inventory });

        Assert.DoesNotContain("Synthetic Person", json, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.invalid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic account", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RowIdentifier", json, StringComparison.Ordinal);
        Assert.Contains("uia:42:2", json, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SafeUiAnalysis_ReturnsOnlyBoundedAggregateMetadata()
    {
        var snapshot = new UiSnapshot(
            7,
            "uia:7:1",
            DateTimeOffset.UtcNow,
            [
                Element("uia:7:1", null, "Table", "Private grid heading", "OrdersGrid", ["Grid"]),
                Element("uia:7:2", "uia:7:1", "DataItem", "Private row", "OrderRow", ["SelectionItem"]),
                Element("uia:7:3", "uia:7:2", "TreeItem", "Private node", "TreeNode", ["ExpandCollapse"])
            ],
            false,
            100);

        var grid = SafeUiAnalysis.GridSummary(snapshot);
        var tree = SafeUiAnalysis.TreeSummary(snapshot);
        var items = SafeUiAnalysis.ItemsSummary(snapshot);

        Assert.AreEqual(1, grid.RowCount);
        Assert.AreEqual(1, tree.NodeCount);
        Assert.AreEqual(2, items.ItemCount);
        Assert.IsTrue(grid.MetadataOnly);
        Assert.IsTrue(tree.MetadataOnly);
        Assert.IsTrue(items.MetadataOnly);
    }

    [TestMethod]
    public void SelectorAudit_ExcludesNativeSystemMenuProviderChrome()
    {
        var snapshot = new UiSnapshot(
            9,
            "uia:9:1",
            DateTimeOffset.UtcNow,
            [
                Element("uia:9:1", null, "Window", "Synthetic window", "WindowRoot", ["Window"]),
                Element("uia:9:2", "uia:9:1", "MenuItem", "System", "", ["Invoke"]),
                Element("uia:9:3", "uia:9:1", "Button", "Synthetic action", "SyntheticActionButton", ["Invoke"])
            ],
            false,
            100);

        var audit = SafeUiAnalysis.SelectorAudit(snapshot);

        Assert.AreEqual(1, audit.ActionableElementCount);
        Assert.AreEqual(1, audit.StableSelectorCount);
        Assert.AreEqual(0, audit.MissingAutomationIdCount);
    }

    private static UiElementSnapshot Element(
        string reference,
        string? parent,
        string controlType,
        string name,
        string automationId,
        IReadOnlyList<string> patterns)
        => new(reference, parent, controlType, name, automationId, "SyntheticClass", "WPF",
            new RectDto(0, 0, 100, 30), true, false, true, false, patterns, parent is null ? 0 : 1);
}
