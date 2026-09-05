using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.BuiltIn;

namespace AgentIsland.Tests;

public class AgentCatalogTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    [Fact]
    public void TestBuiltInAgentCatalog()
    {
        RunAll();
    }

    internal static void RunAll()
    {
        var catalog = new BuiltInAgentCatalog();
        var keys = catalog.Modules.Select(module => module.Descriptor.Key.Value).ToArray();

        Expect(keys.SequenceEqual(new[] { "claude", "codex", "antigravity", "grok", "cursor", "deepseek" }),
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
        Expect(catalog.Find(new AgentKey("deepseek"))!.Descriptor.Supports(AgentCapabilities.Cost),
            "DeepSeek advertises local cost data");
        Expect(catalog.Find(new AgentKey("deepseek"))!.Descriptor.Supports(AgentCapabilities.Activity),
            "DeepSeek advertises local Harness activity data");
        Expect(!catalog.Find(new AgentKey("deepseek"))!.Descriptor.Supports(AgentCapabilities.Usage),
            "DeepSeek does not advertise unsupported quota data");

        Console.WriteLine("AgentCatalogTests GREEN");
    }
}
