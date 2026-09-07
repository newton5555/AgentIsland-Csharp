using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Usage;

namespace AgentIsland.Backend.Workers;

/// <summary>
/// Background worker hosted by Generic Host that periodically aggregates provider costs.
/// </summary>
public sealed class CostAggregationWorker : BackgroundService
{
    private readonly ICostStore _costStore;
    private readonly RefreshIntervalStore? _refreshIntervalStore;
    private readonly ILogger<CostAggregationWorker>? _logger;

    public CostAggregationWorker(
        ICostStore costStore,
        RefreshIntervalStore? refreshIntervalStore = null,
        ILogger<CostAggregationWorker>? logger = null)
    {
        _costStore = costStore;
        _refreshIntervalStore = refreshIntervalStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("CostAggregationWorker starting.");
        if (_costStore is CostStore store)
        {
            store.DisableInternalTimer = true;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
            {
                await _costStore.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalSeconds = _refreshIntervalStore?.Seconds ?? 300;
            if (intervalSeconds < 10) intervalSeconds = 300;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            try
            {
                if (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    await _costStore.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger?.LogInformation("CostAggregationWorker stopped.");
    }
}
