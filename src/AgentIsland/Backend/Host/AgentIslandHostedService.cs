using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentIsland.Backend.Host;

/// <summary>
/// Root application hosted service managing background workers and lifecycle.
/// </summary>
public sealed class AgentIslandHostedService : IHostedService
{
    private readonly ILogger<AgentIslandHostedService>? _logger;

    public AgentIslandHostedService(ILogger<AgentIslandHostedService>? logger = null)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("AgentIsland generic host started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("AgentIsland generic host stopping.");
        return Task.CompletedTask;
    }
}
