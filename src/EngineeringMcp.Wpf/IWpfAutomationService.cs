using EngineeringMcp.Contracts;

namespace EngineeringMcp.Wpf;

/// <summary>
/// Automation seam for diagnosis orchestration: the minimal UIA surface needed to resolve an
/// element, take a bounded snapshot, and perform a validated click. Implemented by
/// <see cref="WpfAutomationService"/>; consumed via dependency injection so orchestration code
/// can be exercised against a test double without a live UIA session.
/// </summary>
public interface IWpfAutomationService
{
    /// <summary>Resolves a UI element in an allowlisted process using the given selector.</summary>
    ToolResult<UiElementSnapshot> Query(int processId, UiSelector selector);

    /// <summary>Captures a bounded UI snapshot of an allowlisted, attached process.</summary>
    ToolResult<UiSnapshot> Snapshot(int processId, string? windowReference = null, int maxElements = 500, int maxDepth = 12);

    /// <summary>Invokes the element resolved by the selector in an allowlisted, attached process.</summary>
    ToolResult<object> Click(int processId, UiSelector selector);
}
