using AgentIsland.Core.Agents;

namespace AgentIsland.Providers.BuiltIn;

/// The first migration keeps registration explicit and local. A future agent
/// is added by one descriptor here and its optional adapters under its own
/// provider folder; dynamic plugin loading is intentionally not needed yet.
public sealed class BuiltInAgentCatalog : IAgentCatalog
{
    private readonly Dictionary<AgentKey, IAgentModule> _byKey;

    public BuiltInAgentCatalog()
    {
        Modules = CreateModules();
        _byKey = Modules.ToDictionary(module => module.Descriptor.Key);
    }

    public IReadOnlyList<IAgentModule> Modules { get; }

    public IAgentModule? Find(AgentKey key) =>
        _byKey.TryGetValue(key, out var module) ? module : null;

    private static IReadOnlyList<IAgentModule> CreateModules() =>
    [
        Module("claude", "Claude", AgentCapabilities.Activity | AgentCapabilities.Usage |
            AgentCapabilities.Cost | AgentCapabilities.SessionNavigation |
            AgentCapabilities.Reauthentication, "claude"),
        Module("codex", "Codex", AgentCapabilities.Activity | AgentCapabilities.Usage |
            AgentCapabilities.Cost | AgentCapabilities.SessionNavigation |
            AgentCapabilities.Reauthentication, "codex"),
        Module("antigravity", "Antigravity", AgentCapabilities.Activity | AgentCapabilities.Usage |
            AgentCapabilities.SessionNavigation, "agy"),
        Module("grok", "Grok", AgentCapabilities.Activity | AgentCapabilities.Usage |
            AgentCapabilities.Cost | AgentCapabilities.SessionNavigation, "grok"),
        Module("cursor", "Cursor", AgentCapabilities.Activity | AgentCapabilities.Usage |
            AgentCapabilities.Cost | AgentCapabilities.SessionNavigation),
    ];

    private static IAgentModule Module(
        string key,
        string displayName,
        AgentCapabilities capabilities,
        string? cliName = null) =>
        new BuiltInAgentModule(new AgentDescriptor(key, displayName, capabilities, cliName));
}
