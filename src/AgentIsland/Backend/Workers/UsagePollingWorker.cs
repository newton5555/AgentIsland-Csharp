using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentIsland.Backend.Usage;
using AgentIsland.Core.Options;

namespace AgentIsland.Backend.Workers;

/// <summary>
/// Background worker hosted by Generic Host that periodically refreshes provider usage.
/// Utilizes .NET 8 PeriodicTimer and observes user-configured refresh intervals via IOptionsMonitor.
/// </summary>
public sealed class UsagePollingWorker : BackgroundService
{
    private readonly IUsageStore _usageStore;
    private readonly RefreshIntervalStore? _refreshIntervalStore;
    private readonly IOptionsMonitor<PollingOptions>? _pollingOptions;
    private readonly ILogger<UsagePollingWorker>? _logger;

    public UsagePollingWorker(
        IUsageStore usageStore,
        RefreshIntervalStore? refreshIntervalStore = null,
        IOptionsMonitor<PollingOptions>? pollingOptions = null,
        ILogger<UsagePollingWorker>? logger = null)
    {
        _usageStore = usageStore;
        _refreshIntervalStore = refreshIntervalStore;
        _pollingOptions = pollingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("UsagePollingWorker starting.");
        if (_usageStore is UsageStore store)
        {
            store.DisableInternalTimer = true;
        }

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
            var intervalSeconds = _pollingOptions?.CurrentValue.RefreshIntervalSeconds
                ?? _refreshIntervalStore?.Seconds
                ?? 300;
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
