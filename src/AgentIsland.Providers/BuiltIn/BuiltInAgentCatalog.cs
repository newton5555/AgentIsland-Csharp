using AgentIsland.Core.Agents;

namespace AgentIsland.Providers.BuiltIn;

/// The first migration keeps registration explicit and local. A future agent
/// is added by one descriptor here and its optional adapters under its own
/// provider folder; dynamic plugin loading is intentionally not needed yet.
public sealed class BuiltInAgentCatalog : IAgentCatalog
{
    private readonly Dictionary<AgentKey, IAgentModule> _byKey;
    private readonly List<IAgentModule> _modules;

    public BuiltInAgentCatalog(IEnumerable<IAgentModule>? modules = null)
    {
        _modules = modules?.ToList() ?? CreateModules().ToList();
        _byKey = _modules.ToDictionary(module => module.Descriptor.Key);
    }

    public IReadOnlyList<IAgentModule> Modules => _modules;

    public IAgentModule? Find(AgentKey key) =>
        _byKey.TryGetValue(key, out var module) ? module : null;

    public void Register(IAgentModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _byKey[module.Descriptor.Key] = module;
        var existingIndex = _modules.FindIndex(m => m.Descriptor.Key == module.Descriptor.Key);
        if (existingIndex >= 0)
        {
            _modules[existingIndex] = module;
        }
        else
        {
            _modules.Add(module);
        }
    }


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
        // DeepSeek Harness exposes a local token ledger and a compressed
        // activity stream. It deliberately does not claim quota, navigation,
        // or re-auth support; balance is an optional official API card.
        Module("deepseek", "DeepSeek", AgentCapabilities.Activity | AgentCapabilities.Cost),
    ];

    private static IAgentModule Module(
        string key,
        string displayName,
        AgentCapabilities capabilities,
        string? cliName = null) =>
        new BuiltInAgentModule(new AgentDescriptor(key, displayName, capabilities, cliName));
}
