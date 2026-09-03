using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.BuiltIn;

namespace AgentIsland.Tests;

public static class AgentCatalogTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    public static void RunAll()
    {
        var catalog = new BuiltInAgentCatalog();
        var keys = catalog.Modules.Select(module => module.Descriptor.Key.Value).ToArray();

        Expect(keys.SequenceEqual(new[] { "claude", "codex", "antigravity", "grok", "cursor" }),
            "built-in Agent order is stable");
        Expect(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length,
            "built-in Agent keys are unique");
        Expect(catalog.Find(new AgentKey(" CODEX "))?.Descriptor.DisplayName == "Codex",
            "Agent lookup normalizes stable keys");
        Expect(catalog.Find(new AgentKey("new-agent")) is null,
            "unknown Agent is absent instead of receiving fake capabilities");
        Expect(catalog.Find(new AgentKey("claude"))!.Descriptor.Supports(AgentCapabilities.Activity),
            "Claude advertises activity capability");
        Expect(!catalog.Find(new AgentKey("antigravity"))!.Descriptor.Supports(AgentCapabilities.Cost),
            "Antigravity does not advertise unsupported cost data");

        Console.WriteLine("AgentCatalogTests GREEN");
    }
}
