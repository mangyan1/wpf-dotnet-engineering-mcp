using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed class UiActionRiskClassifier(FilePolicyProvider policyProvider)
{
    private static readonly string[] BuiltInDestructiveTerms =
        ["delete", "remove", "erase", "wipe", "format", "purge", "revoke", "factory reset", "uninstall"];

    private static readonly string[] BuiltInStatefulTerms =
        ["save", "apply", "connect", "disconnect", "add", "create", "submit", "install", "update", "upload", "download", "start", "stop", "restart"];

    public ToolResult<RiskClass> Classify(UiElementSnapshot element)
    {
        var policy = policyProvider.Current.UiActions;
        if (MatchesId(policy.DenyAutomationIds, element.AutomationId))
            return ToolResult<RiskClass>.Fail("UI_ACTION_DENIED", "Target AutomationId is explicitly denied by UI action policy.");
        if (MatchesId(policy.DestructiveAutomationIds, element.AutomationId))
            return ToolResult<RiskClass>.Ok(RiskClass.Destructive);
        if (MatchesId(policy.StatefulAutomationIds, element.AutomationId))
            return ToolResult<RiskClass>.Ok(RiskClass.StatefulMutation);

        var semantic = $"{element.Name} {element.AutomationId}";
        if (BuiltInDestructiveTerms.Any(x => semantic.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return ToolResult<RiskClass>.Ok(RiskClass.Destructive);
        if (BuiltInStatefulTerms.Any(x => semantic.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return ToolResult<RiskClass>.Ok(RiskClass.StatefulMutation);
        // An unrecognized click can still submit a form, navigate, or trigger application logic.
        // Classify conservatively; only explicitly implemented non-click operations use SafeMutation.
        return ToolResult<RiskClass>.Ok(RiskClass.StatefulMutation);
    }

    private static bool MatchesId(IReadOnlyList<string> ids, string value)
        => ids.Any(x => string.Equals(x, value, StringComparison.Ordinal));
}
