using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Host;

internal static class ToolPolicies
{
    public static ToolPolicy Read(string tool, string capability)
        => Require(tool, PermissionLevel.UiRead, capability).ToPolicy();

    public static ToolPolicy UiMutate(string tool, RiskClass risk = RiskClass.StatefulMutation)
        => Require(tool, PermissionLevel.UiInteraction, "wpf.uia.interact").ToPolicy(risk);

    public static ToolPolicy Diagnose(string tool, string capability = "dotnet.eventpipe")
        => Require(tool, PermissionLevel.ApplicationDiagnostics, capability).ToPolicy();

    public static ToolPolicy Privileged(string tool)
        => Require(tool, PermissionLevel.SensitiveDiagnostics, "dotnet.clrmd").ToPolicy();

    private static ToolAccessDefinition Require(string tool, PermissionLevel permission, string capability)
    {
        var definition = ToolPolicyCatalog.Get(tool);
        if (definition.RequiredPermission != permission ||
            !string.Equals(definition.CapabilityId, capability, StringComparison.Ordinal))
            throw new InvalidOperationException($"Tool policy catalogue mismatch for '{tool}'.");
        return definition;
    }
}
