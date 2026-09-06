using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentIsland.Backend.Updates;
using AgentIsland.Core;

namespace AgentIsland.Backend.Workers;

/// <summary>
/// Background worker hosted by Generic Host that periodically checks for application updates.
/// </summary>
public sealed class UpdateCheckWorker : BackgroundService
{
    private readonly IUpdateChecker _updateChecker;
    private readonly ILogger<UpdateCheckWorker>? _logger;

    public UpdateCheckWorker(
        IUpdateChecker updateChecker,
        ILogger<UpdateCheckWorker>? logger = null)
    {
        _updateChecker = updateChecker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("UpdateCheckWorker starting.");

        if (AppEnvironment.Current != AppMode.Normal) return;

        // Initial delay 20 seconds
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
            {
                await _updateChecker.CheckAsync(userInitiated: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await _updateChecker.CheckAsync(userInitiated: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }

        _logger?.LogInformation("UpdateCheckWorker stopped.");
    }
}
