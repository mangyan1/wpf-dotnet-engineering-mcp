using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed record ToolAccessDefinition(
    string ToolName,
    string Profile,
    PermissionLevel RequiredPermission,
    RiskClass Risk,
    string CapabilityId,
    bool TargetRiskIsDynamic = false)
{
    public ToolPolicy ToPolicy(RiskClass? risk = null)
        => new(ToolName, RequiredPermission, risk ?? Risk, CapabilityId);
}

public sealed record ToolPublicationDecision(
    bool Published,
    string Code,
    string Reason,
    string? Remediation = null);

/// <summary>
/// Central policy metadata for every public MCP tool. Tool discovery, preflight reporting, and
/// execution policy construction share this catalogue so an agent cannot infer access from a
/// permission level or profile name alone.
/// </summary>
public static class ToolPolicyCatalog
{
    private static readonly IReadOnlyDictionary<string, ToolAccessDefinition> Definitions = BuildDefinitions();

    public static IEnumerable<ToolAccessDefinition> All => Definitions.Values;

    public static bool TryGet(string toolName, out ToolAccessDefinition definition)
        => Definitions.TryGetValue(toolName, out definition!);

    public static ToolAccessDefinition Get(string toolName)
        => Definitions.TryGetValue(toolName, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "Unknown Engineering MCP tool name.");

    public static ToolPublicationDecision Publication(string toolName, McpPolicy policy)
    {
        if (!TryGet(toolName, out var definition))
            return new(false, "UNKNOWN_TOOL", $"Tool '{toolName}' is not in the Engineering MCP contract.",
                "Call tools/list and use an exact published tool name.");

        if (policy.DisabledTools?.Contains(toolName, StringComparer.Ordinal) == true)
            return new(false, "TOOL_DISABLED", $"Tool '{toolName}' is listed in disabledTools.",
                $"Keep the denial or remove '{toolName}' from disabledTools in an approved policy, then restart the MCP server.");

        if (policy.EnabledTools is { Count: > 0 } &&
            !policy.EnabledTools.Contains(toolName, StringComparer.Ordinal))
            return new(false, "TOOL_NOT_ENABLED", $"Tool '{toolName}' is not present in enabledTools.",
                $"Add '{toolName}' to enabledTools in an approved policy, then restart the MCP server.");

        if (policy.EnabledToolProfiles is { Count: > 0 } &&
            !policy.EnabledToolProfiles.Contains(definition.Profile, StringComparer.OrdinalIgnoreCase))
            return new(false, "PROFILE_DISABLED",
                $"Tool '{toolName}' belongs to profile '{definition.Profile}', which is not enabled.",
                $"Enable the approved '{definition.Profile}' profile, then restart the MCP server.");

        return new(true, "PUBLISHED", $"Tool '{toolName}' is published by the active tool policy.");
    }

    private static IReadOnlyDictionary<string, ToolAccessDefinition> BuildDefinitions()
    {
        var result = new Dictionary<string, ToolAccessDefinition>(StringComparer.Ordinal);

        Add(result, "core", PermissionLevel.Metadata, RiskClass.Read, "system.metadata",
            "system_version", "system_health", "system_capabilities");
        Add(result, "core", PermissionLevel.Metadata, RiskClass.Read, "security.policy",
            "system_permissions", "system_policy_diagnostics", "system_tool_preflight");

        Add(result, "wpf-read", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read",
            "wpf_list_processes", "wpf_list_windows", "wpf_snapshot", "wpf_find", "wpf_query",
            "wpf_wait", "wpf_assert", "wpf_wait_absent", "wpf_wait_hidden", "wpf_wait_disabled",
            "wpf_assert_exists", "wpf_assert_not_exists", "wpf_assert_pattern", "wpf_selector_audit",
            "wpf_duplicate_automation_ids", "wpf_control_inventory", "wpf_pattern_inventory",
            "wpf_grid_summary", "wpf_tree_summary", "wpf_items_summary", "wpf_accessibility_summary",
            "wpf_window_state");
        Add(result, "wpf-interact", PermissionLevel.UiRead, RiskClass.Read, "wpf.uia.read",
            "wpf_attach", "wpf_detach");
        Add(result, "wpf-read", PermissionLevel.UiRead, RiskClass.Read, "wpf.screenshot.redacted",
            "wpf_screenshot");
        Add(result, "wpf-interact", PermissionLevel.UiInteraction, RiskClass.StatefulMutation, "wpf.uia.interact",
            "wpf_type", "wpf_select", "wpf_toggle", "wpf_expand", "wpf_collapse");
        Add(result, "wpf-interact", PermissionLevel.UiInteraction, RiskClass.SafeMutation, "wpf.uia.interact",
            "wpf_scroll", "wpf_focus");
        result.Add("wpf_click", new("wpf_click", "wpf-interact", PermissionLevel.UiInteraction,
            RiskClass.StatefulMutation, "wpf.uia.interact", TargetRiskIsDynamic: true));

        Add(result, "wpf-read", PermissionLevel.UiRead, RiskClass.Read, "a11y.audit", "a11y_audit");
        Add(result, "wpf-read", PermissionLevel.UiRead, RiskClass.Read, "gui.audit", "gui_audit");
        Add(result, "wpf-read", PermissionLevel.UiRead, RiskClass.Read, "ux.heuristics", "ux_review");

        Add(result, "wpf-read", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "wpf.probe",
            "wpf_probe", "wpf_binding_info", "wpf_binding_errors", "wpf_command_state",
            "wpf_validation_summary", "wpf_datacontext_type", "wpf_dispatcher_status");
        Add(result, "wpf-read", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "wpfui.resources",
            "wpfui_inspect");

        Add(result, "source", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "source.roslyn",
            "source_inventory", "source_read", "source_find_symbol", "source_find_references",
            "source_find_references_page", "source_find_references_semantic", "source_map_stacktrace");
        Add(result, "source", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "source.xaml",
            "source_analyze_xaml", "source_find_automation_id", "source_find_binding");
        Add(result, "source", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "wpfui.static_audit",
            "wpfui_audit_resources");

        Add(result, "diagnostics", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "dotnet.eventpipe",
            "dotnet_runtime_info", "dotnet_counters", "dotnet_gc_summary", "dotnet_threads",
            "dotnet_modules", "dotnet_exceptions", "dotnet_trace_stop");
        Add(result, "diagnostics", PermissionLevel.ApplicationDiagnostics, RiskClass.StatefulMutation, "dotnet.eventpipe",
            "dotnet_trace_start");
        Add(result, "diagnostics", PermissionLevel.SensitiveDiagnostics, RiskClass.Privileged, "dotnet.clrmd",
            "dotnet_capture_dump", "dotnet_analyze_dump");
        Add(result, "diagnostics", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "aspnet.telemetry",
            "aspnet_health", "aspnet_requests", "aspnet_exceptions");
        Add(result, "diagnostics", PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "diagnose.correlation",
            "diagnose");
        result.Add("diagnose_click", new("diagnose_click", "diagnostics", PermissionLevel.ApplicationDiagnostics,
            RiskClass.StatefulMutation, "diagnose.correlation", TargetRiskIsDynamic: true));

        return result;
    }

    private static void Add(
        IDictionary<string, ToolAccessDefinition> destination,
        string profile,
        PermissionLevel permission,
        RiskClass risk,
        string capability,
        params string[] names)
    {
        foreach (var name in names)
            destination.Add(name, new(name, profile, permission, risk, capability));
    }
}
