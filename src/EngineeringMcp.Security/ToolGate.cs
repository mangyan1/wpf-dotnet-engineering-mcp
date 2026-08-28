using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed class ToolGate(PolicyEngine engine, FilePolicyProvider provider)
{
    public PolicyDecision Authorize(ToolPolicy policy, bool capabilityAvailable)
        => engine.Authorize(policy, provider.Current, capabilityAvailable);
}
