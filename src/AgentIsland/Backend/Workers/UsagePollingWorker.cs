using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentIsland.Backend.Usage;

namespace AgentIsland.Backend.Workers;

/// <summary>
/// Background worker hosted by Generic Host that periodically refreshes provider usage.
/// Utilizes .NET 8 PeriodicTimer and observes user-configured refresh intervals.
/// </summary>
public sealed class UsagePollingWorker : BackgroundService
{
    private readonly IUsageStore _usageStore;
    private readonly ILogger<UsagePollingWorker>? _logger;

    public UsagePollingWorker(
        IUsageStore usageStore,
        ILogger<UsagePollingWorker>? logger = null)
    {
        _usageStore = usageStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("UsagePollingWorker starting.");

        // Initial delay so startup does not race network association
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
            {
                await _usageStore.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = RefreshIntervalStore.Shared.Seconds;
            if (intervalSeconds < 5) intervalSeconds = 300;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            try
            {
                if (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    await _usageStore.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger?.LogInformation("UsagePollingWorker stopped.");
    }
}
