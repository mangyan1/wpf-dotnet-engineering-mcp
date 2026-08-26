using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public interface IToolGate
{
    PolicyDecision Authorize(ToolPolicy policy, bool capabilityAvailable);
}

public sealed class ToolGate(IPolicyEngine engine, IPolicyProvider provider) : IToolGate
{
    public PolicyDecision Authorize(ToolPolicy policy, bool capabilityAvailable)
        => engine.Authorize(policy, provider.Current, capabilityAvailable);
}
