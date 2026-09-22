using Microsoft.Extensions.DependencyInjection;
using AgentIsland.Core.Agents;
using AgentIsland.Backend.Cost.Adapters;

namespace AgentIsland.Backend.Providers;

public static class AgentProviderExtensions
{
    public static IServiceCollection AddAgentProviders(this IServiceCollection services)
    {
        services.AddSingleton<IAgentProvider, ClaudeAgentProvider>();
        services.AddSingleton<IAgentProvider>(sp => new CodexAgentProvider(sp.GetService<CodexCostLedgerReader>()));
        services.AddSingleton<IAgentProvider, AntigravityAgentProvider>();
        services.AddSingleton<IAgentProvider, DeepSeekAgentProvider>();
        services.AddSingleton<IAgentProvider, GrokAgentProvider>();
        services.AddSingleton<IAgentProvider, CursorAgentProvider>();

        return services;
    }
}
