using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Wpf;

namespace EngineeringMcp.Wpf;

public sealed class WpfUiInspectionService(WpfProbeClient probe)
{
    public async Task<ToolResult<object>> GetResourceAsync(int processId, string automationId, string resourceKey, CancellationToken cancellationToken = default)
        => Convert(await probe.RequestAsync(processId, new ProbeRequest(string.Empty, "resource", AutomationId: automationId, ResourceKey: resourceKey), cancellationToken).ConfigureAwait(false));

    public async Task<ToolResult<object>> GetPropertyAsync(int processId, string automationId, string property, CancellationToken cancellationToken = default)
        => Convert(await probe.RequestAsync(processId, new ProbeRequest(string.Empty, "property", AutomationId: automationId, Property: property), cancellationToken).ConfigureAwait(false));

    public async Task<ToolResult<object>> GetThemeEvidenceAsync(int processId, CancellationToken cancellationToken = default)
    {
        string[] keys = ["ApplicationTheme", "SystemTheme", "TextFillColorPrimaryBrush", "ControlFillColorDefaultBrush"];
        var observations = new List<object>();
        foreach (var key in keys)
        {
            var result = await probe.RequestAsync(processId, new ProbeRequest(string.Empty, "resource", ResourceKey: key), cancellationToken).ConfigureAwait(false);
            if (result.Success && result.Value?.Success == true)
                observations.Add(new { key, value = result.Value.Value });
        }
        return ToolResult<object>.Ok(new
        {
            evidence = observations,
            note = "Theme is reported only from observed resource evidence; no theme is inferred when known keys are absent."
        });
    }

    private static ToolResult<object> Convert(ToolResult<ProbeResponse> result)
    {
        if (!result.Success || result.Value is null) return ToolResult<object>.Fail(result.Error!.Code, result.Error.Message, result.Error.Retryable);
        if (!result.Value.Success) return ToolResult<object>.Fail(result.Value.ErrorCode ?? "PROBE_FAILED", result.Value.ErrorMessage ?? "Probe operation failed.");
        return ToolResult<object>.Ok(result.Value.Value ?? JsonDocument.Parse("null").RootElement);
    }
}
