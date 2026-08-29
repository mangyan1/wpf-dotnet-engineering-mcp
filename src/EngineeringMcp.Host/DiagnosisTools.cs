using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.FailureCorrelation;
using EngineeringMcp.Security;
using ModelContextProtocol.Server;
using ModelContextProtocol;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class DiagnosisTools
{
    [McpServerTool(Name = "diagnose", UseStructuredContent = true), Description("Read-only cross-layer diagnosis of an attached WPF process: current WPF element and UI state, optional WPF probe evidence, backend observations, and approved source correlation. Timing correlation is CORRELATED, never causal fact.")]
    public static Task<ToolResult<DiagnosisReport>> Diagnose(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        DiagnosisService diagnosis,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        IProgress<ProgressNotificationValue> progress,
        [Description("Process id of an attached ASP.NET diagnostic adapter to correlate backend observations; omit when backend evidence is not needed.")] int? backendProcessId = null,
        [Description("Approved source root beneath policy allowlists used to correlate source evidence; omit when source correlation is not needed.")] string? sourceRoot = null,
        [Description("AutomationId of the target element; narrows the WPF evidence collected.")] string? automationId = null,
        [Description("Element name of the target element; narrows the WPF evidence collected.")] string? name = null,
        [Description("Control type of the target element, e.g. Button or TextBox.")] string? controlType = null,
        [Description("Previously returned opaque element reference used to re-select the same element.")] string? reference = null)
    {
        var selector = new UiSelector(Reference: reference, AutomationId: automationId, Name: name, ControlType: controlType);
        return ToolRun.Async(auth, ToolPolicyCatalog.Get("diagnose").ToPolicy(), processId.ToString(), async () =>
        {
            progress.Report(new ProgressNotificationValue { Progress = 0, Total = 100, Message = "Collecting current WPF and diagnostic evidence." });
            var result = await diagnosis.DiagnoseObserveAsync(processId, selector, backendProcessId, sourceRoot, cancellationToken).ConfigureAwait(false);
            progress.Report(new ProgressNotificationValue { Progress = 100, Total = 100, Message = "Evidence collection completed." });
            return result;
        });
    }

    [McpServerTool(Name = "diagnose_click", UseStructuredContent = true), Description("Evidence-first workflow: resolves a WPF element, observes EventPipe, clicks it, inspects resulting UI, optionally correlates configured ASP.NET observations and approved source. Timing correlation is explicitly labeled CORRELATED, never causal fact.")]
    public static async Task<ToolResult<DiagnosisReport>> DiagnoseClick(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        DiagnosisService diagnosis,
        EngineeringMcp.Wpf.WpfAutomationService wpf,
        UiActionRiskClassifier classifier,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("AutomationId of the element to click; at least one selector argument is required.")] string? automationId = null,
        [Description("Element name of the element to click; used when no AutomationId is available.")] string? name = null,
        [Description("Control type of the element to click, e.g. Button or MenuItem.")] string? controlType = null,
        [Description("Previously returned opaque element reference used to re-select the same element.")] string? reference = null,
        [Description("Process id of an attached ASP.NET diagnostic adapter to correlate backend observations; omit when backend evidence is not needed.")] int? backendProcessId = null,
        [Description("Approved source root beneath policy allowlists used to correlate source evidence; omit when source correlation is not needed.")] string? sourceRoot = null,
        [Description("Bounded EventPipe observation window around the click, in milliseconds; the server applies a hard upper bound.")] int observationWindowMs = 5_000)
    {
        var selector = new UiSelector(Reference: reference, AutomationId: automationId, Name: name, ControlType: controlType);
        // The pre-mutation inspect is authorized under the public diagnose_click tool name so that a
        // policy allowlisting diagnose_click does not silently deny the whole operation.
        var readPolicy = new ToolPolicy("diagnose_click", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read");
        var readAllowed = auth.Authorize(readPolicy, processId.ToString());
        if (!readAllowed.Success) return ToolResult<DiagnosisReport>.Fail(readAllowed.Error!.Code, readAllowed.Error.Message);
        var inspected = wpf.Query(processId, selector);
        auth.Complete(readAllowed.Value!, readPolicy, processId.ToString(), inspected.Success, inspected.Success ? "OK" : inspected.Error?.Code ?? "FAILED");
        if (!inspected.Success || inspected.Value is null) return ToolResult<DiagnosisReport>.Fail(inspected.Error!.Code, inspected.Error.Message);
        var risk = classifier.Classify(inspected.Value);
        if (!risk.Success) return ToolResult<DiagnosisReport>.Fail(risk.Error!.Code, risk.Error.Message);

        var policy = ToolPolicyCatalog.Get("diagnose_click").ToPolicy(risk.Value);
        return await ToolRun.Async(auth, policy, processId.ToString(),
            () => diagnosis.DiagnoseClickAsync(processId, selector, backendProcessId, sourceRoot, observationWindowMs, cancellationToken));
    }
}
