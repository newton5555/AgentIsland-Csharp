namespace AgentIsland.Core.Agents;

/// Stable string identity for an agent integration. This is deliberately not
/// an enum: adding a new agent should not require changing a shared switch or
/// renumbering persisted values.
public readonly record struct AgentKey
{
    public AgentKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Agent key cannot be empty.", nameof(value));

        Value = value.Trim().ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator AgentKey(string value) => new(value);
}

[Flags]
public enum AgentCapabilities
{
    None = 0,
    Activity = 1 << 0,
    Usage = 1 << 1,
    Cost = 1 << 2,
    SessionNavigation = 1 << 3,
    Reauthentication = 1 << 4,
}

/// Metadata used by orchestration and presentation. Provider-specific code
/// can add optional adapters later without making every agent implement every
/// capability.
public sealed record AgentDescriptor(
    AgentKey Key,
    string DisplayName,
    AgentCapabilities Capabilities,
    string? CliName = null)
{
    public bool Supports(AgentCapabilities capability) =>
        (Capabilities & capability) == capability;
}

public interface IAgentModule
{
    AgentDescriptor Descriptor { get; }
}

public interface IAgentCatalog
{
    IReadOnlyList<IAgentModule> Modules { get; }

    IAgentModule? Find(AgentKey key);
}

public static class AgentCatalogExtensions
{
    public static IEnumerable<IAgentProvider> Providers(this IAgentCatalog catalog) =>
        catalog.Modules.OfType<IAgentProvider>();

    public static IAgentProvider? FindProvider(this IAgentCatalog catalog, AgentKey key) =>
        catalog.Find(key) as IAgentProvider;
}

