using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentIsland.Backend.Updates;
using AgentIsland.Core;
using AgentIsland.Core.Options;

namespace AgentIsland.Backend.Workers;

/// <summary>
/// Background worker hosted by Generic Host that periodically checks for application updates.
/// </summary>
public sealed class UpdateCheckWorker : BackgroundService
{
    private readonly IUpdateChecker _updateChecker;
    private readonly IOptionsMonitor<PollingOptions>? _options;
    private readonly ILogger<UpdateCheckWorker>? _logger;

    public UpdateCheckWorker(
        IUpdateChecker updateChecker,
        IOptionsMonitor<PollingOptions>? options = null,
        ILogger<UpdateCheckWorker>? logger = null)
    {
        _updateChecker = updateChecker;
        _options = options;
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
            if (!stoppingToken.IsCancellationRequested && (_options?.CurrentValue.AutoCheckUpdates ?? true))
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
                if (_options?.CurrentValue.AutoCheckUpdates ?? true)
                {
                    await _updateChecker.CheckAsync(userInitiated: false).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }

        _logger?.LogInformation("UpdateCheckWorker stopped.");
    }
}
