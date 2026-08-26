using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Diagnosis;
using EngineeringMcp.Security;
using ModelContextProtocol.Server;
using ModelContextProtocol;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class DiagnosisTools
{
    [McpServerTool(Name = "diagnose_observe", UseStructuredContent = true), Description("Read-only evidence collection across the selected WPF element, current UI state, optional WPF probe, backend observations, and approved source. It performs no UI action.")]
    public static Task<ToolResult<DiagnosisReport>> DiagnoseObserve(int processId, string? automationId, string? name, string? controlType, string? reference, int? backendProcessId, string? sourceRoot, IDiagnosisService diagnosis, IToolAuthorization auth, CancellationToken cancellationToken, IProgress<ProgressNotificationValue> progress)
        => RunObserve("diagnose_observe", processId, automationId, name, controlType, reference, backendProcessId, sourceRoot, diagnosis, auth, cancellationToken, progress);

    [McpServerTool(Name = "diagnose_failure", UseStructuredContent = true), Description("Read-only failure triage that gathers current WPF binding, validation, exception, UI, backend, trace-id, and approved-source evidence without replaying the failing action.")]
    public static Task<ToolResult<DiagnosisReport>> DiagnoseFailure(int processId, string? automationId, string? name, string? controlType, string? reference, int? backendProcessId, string? sourceRoot, IDiagnosisService diagnosis, IToolAuthorization auth, CancellationToken cancellationToken, IProgress<ProgressNotificationValue> progress)
        => RunObserve("diagnose_failure", processId, automationId, name, controlType, reference, backendProcessId, sourceRoot, diagnosis, auth, cancellationToken, progress);

    [McpServerTool(Name = "diagnose_workflow", UseStructuredContent = true), Description("Read-only cross-layer workflow assessment at the current application state. It correlates only observed identifiers and labels time proximity without claiming causation.")]
    public static Task<ToolResult<DiagnosisReport>> DiagnoseWorkflow(int processId, string? automationId, string? name, string? controlType, string? reference, int? backendProcessId, string? sourceRoot, IDiagnosisService diagnosis, IToolAuthorization auth, CancellationToken cancellationToken, IProgress<ProgressNotificationValue> progress)
        => RunObserve("diagnose_workflow", processId, automationId, name, controlType, reference, backendProcessId, sourceRoot, diagnosis, auth, cancellationToken, progress);

    [McpServerTool(Name = "diagnose_click", UseStructuredContent = true), Description("Evidence-first workflow: resolves a WPF element, observes EventPipe, clicks it, inspects resulting UI, optionally correlates configured ASP.NET observations and approved source. Timing correlation is explicitly labeled CORRELATED, never causal fact.")]
    public static async Task<ToolResult<DiagnosisReport>> DiagnoseClick(
        int processId,
        string? automationId,
        string? name,
        string? controlType,
        string? reference,
        int? backendProcessId,
        string? sourceRoot,
        int observationWindowMs,
        IDiagnosisService diagnosis,
        EngineeringMcp.Wpf.IWpfAutomationService wpf,
        IUiActionRiskClassifier classifier,
        IToolAuthorization auth,
        CancellationToken cancellationToken)
    {
        var selector = new UiSelector(Reference: reference, AutomationId: automationId, Name: name, ControlType: controlType);
        var readPolicy = ToolPolicies.Read("diagnose_click.inspect", "wpf.uia.read");
        var readAllowed = auth.Authorize(readPolicy, processId.ToString());
        if (!readAllowed.Success) return ToolResult<DiagnosisReport>.Fail(readAllowed.Error!.Code, readAllowed.Error.Message);
        var inspected = wpf.Query(processId, selector);
        auth.Complete(readAllowed.Value!, readPolicy, processId.ToString(), inspected.Success, inspected.Success ? "OK" : inspected.Error?.Code ?? "FAILED");
        if (!inspected.Success || inspected.Value is null) return ToolResult<DiagnosisReport>.Fail(inspected.Error!.Code, inspected.Error.Message);
        var risk = classifier.Classify(inspected.Value);
        if (!risk.Success) return ToolResult<DiagnosisReport>.Fail(risk.Error!.Code, risk.Error.Message);
        var policy = new ToolPolicy("diagnose_click", PermissionLevel.ApplicationDiagnostics, risk.Value, "diagnose.correlation");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<DiagnosisReport>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await diagnosis.DiagnoseClickAsync(processId, selector, backendProcessId, sourceRoot, observationWindowMs, cancellationToken).ConfigureAwait(false);
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }

    private static async Task<ToolResult<DiagnosisReport>> RunObserve(string tool, int processId, string? automationId, string? name, string? controlType, string? reference, int? backendProcessId, string? sourceRoot, IDiagnosisService diagnosis, IToolAuthorization auth, CancellationToken cancellationToken, IProgress<ProgressNotificationValue> progress)
    {
        progress.Report(new ProgressNotificationValue { Progress = 0, Total = 100, Message = "Collecting current WPF and diagnostic evidence." });
        var policy = new ToolPolicy(tool, PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "diagnose.correlation");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<DiagnosisReport>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var selector = new UiSelector(Reference: reference, AutomationId: automationId, Name: name, ControlType: controlType);
        var result = await diagnosis.DiagnoseObserveAsync(processId, selector, backendProcessId, sourceRoot, cancellationToken).ConfigureAwait(false);
        progress.Report(new ProgressNotificationValue { Progress = 100, Total = 100, Message = "Evidence collection completed." });
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }
}
