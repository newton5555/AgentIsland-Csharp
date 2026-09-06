using Microsoft.Extensions.DependencyInjection;
using AgentIsland.Core.Agents;

namespace AgentIsland.Backend.Providers;

public static class AgentProviderExtensions
{
    public static IServiceCollection AddAgentProviders(this IServiceCollection services)
    {
        services.AddSingleton<IAgentProvider, ClaudeAgentProvider>();
        services.AddSingleton<IAgentProvider, CodexAgentProvider>();
        services.AddSingleton<IAgentProvider, AntigravityAgentProvider>();
        services.AddSingleton<IAgentProvider, DeepSeekAgentProvider>();
        services.AddSingleton<IAgentProvider, GrokAgentProvider>();
        services.AddSingleton<IAgentProvider, CursorAgentProvider>();

        return services;
    }
}
