using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentIsland.Backend.Monitoring;

namespace AgentIsland.Backend.Workers;

/// <summary>
/// Background worker hosted by Generic Host that drives activity monitoring sweeps.
/// Replaces ad-hoc UI DispatcherTimer polling with modern PeriodicTimer on the thread pool.
/// </summary>
public sealed class ActivityMonitoringWorker : BackgroundService
{
    private readonly IActivityMonitor _activityMonitor;
    private readonly ILogger<ActivityMonitoringWorker>? _logger;

    public ActivityMonitoringWorker(
        IActivityMonitor activityMonitor,
        ILogger<ActivityMonitoringWorker>? logger = null)
    {
        _activityMonitor = activityMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("ActivityMonitoringWorker starting.");
        if (_activityMonitor is ActivityMonitor mon)
        {
            mon.DisableInternalTimer = true;
        }
        _activityMonitor.Start();


        // 6-second heartbeat interval (matching macOS / legacy cadence)
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(6));
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await _activityMonitor.ScanNowAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        finally
        {
            _activityMonitor.Stop();
            _logger?.LogInformation("ActivityMonitoringWorker stopped.");
        }
    }
}
