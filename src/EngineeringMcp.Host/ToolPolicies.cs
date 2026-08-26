using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Host;

internal static class ToolPolicies
{
    public static ToolPolicy Read(string tool, string capability) => new(tool, PermissionLevel.UiRead, RiskClass.Read, capability);
    public static ToolPolicy UiMutate(string tool, RiskClass risk = RiskClass.StatefulMutation) => new(tool, PermissionLevel.UiInteraction, risk, "wpf.uia.interact");
    public static ToolPolicy Diagnose(string tool, string capability = "dotnet.eventpipe") => new(tool, PermissionLevel.ApplicationDiagnostics, RiskClass.Read, capability);
    public static ToolPolicy Privileged(string tool) => new(tool, PermissionLevel.SensitiveDiagnostics, RiskClass.Privileged, "dotnet.clrmd");
}
