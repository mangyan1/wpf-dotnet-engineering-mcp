using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.TestSupport;

/// <summary>
/// Shared test policy holder compiled into every test project: serves one immutable in-memory
/// policy without touching the environment or the file system.
/// </summary>
internal sealed class FixedPolicyProvider(McpPolicy policy) : FilePolicyProvider
{
    public override McpPolicy Current { get; } = policy;
    public override string Source => "test";
}
