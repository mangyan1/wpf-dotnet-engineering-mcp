using EngineeringMcp.Contracts;

namespace EngineeringMcp.Wpf;

public interface IWpfAutomationService : IDisposable
{
    IReadOnlyList<ProcessDescriptor> ListAllowedProcesses();
    ToolResult<object> Attach(int processId);
    ToolResult<IReadOnlyList<WindowDescriptor>> ListWindows(int processId);
    ToolResult<UiSnapshot> Snapshot(int processId, string? windowReference = null, int maxElements = 500, int maxDepth = 12);
    ToolResult<UiElementSnapshot> Find(int processId, UiSelector selector);
    ToolResult<UiElementSnapshot> Query(int processId, UiSelector selector);
    ToolResult<UiElementSnapshot> Wait(int processId, UiSelector selector, int timeoutMs = 5_000, bool requireEnabled = false, bool requireVisible = false, CancellationToken cancellationToken = default);
    ToolResult<UiAssertionResult> Assert(int processId, UiSelector selector, bool? enabled = null, bool? offscreen = null, bool? keyboardFocusable = null, string? expectedName = null);
    ToolResult<object> Click(int processId, UiSelector selector);
    ToolResult<object> TypeText(int processId, UiSelector selector, string text);
    ToolResult<object> Select(int processId, UiSelector selector, string itemText);
    ToolResult<object> Toggle(int processId, UiSelector selector);
    ToolResult<object> Expand(int processId, UiSelector selector);
    ToolResult<object> Collapse(int processId, UiSelector selector);
    ToolResult<object> ScrollIntoView(int processId, UiSelector selector);
    ToolResult<object> Focus(int processId, UiSelector selector);
    ToolResult<SanitizedScreenshot> Screenshot(int processId, UiSelector? selector = null);
    ToolResult<object> Detach(int processId);
}
