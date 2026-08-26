namespace EngineeringMcp.Contracts;

public sealed record CapabilityManifest(
    string ServerVersion,
    IReadOnlyDictionary<string, bool> Capabilities,
    DateTimeOffset GeneratedAtUtc);
