using AgentIsland.Core.Agents;
using AgentMonitoring.Consumption;
using AgentMonitoring.Queries;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentMonitoring.Host;

public sealed class HeadlessCollectWorker : BackgroundService
{
    private readonly IConsumptionCollector _collector;
    private readonly IMonitoringQuery _query;
    private readonly HeadlessOptions _options;
    private readonly HeadlessCollectorHealth _health;
    private readonly ILogger<HeadlessCollectWorker> _logger;

    public HeadlessCollectWorker(
        IConsumptionCollector collector,
        IMonitoringQuery query,
        HeadlessOptions options,
        HeadlessCollectorHealth health,
        ILogger<HeadlessCollectWorker> logger)
    {
        _collector = collector;
        _query = query;
        _options = options;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health.Start();
        try
        {
            await CollectSafely(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_options.CollectInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await CollectSafely(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _health.Stop();
        }
    }

    private async Task CollectSafely(CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        _health.BeginAttempt(attemptedAt);
        try
        {
            await _collector.CollectAsync(AgentKeys.Codex, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            var start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
            var summary = _query.GetConsumptionSummary(start, now.AddDays(1));
            _health.Succeed(DateTimeOffset.UtcNow);
            _logger.LogInformation(
                "Codex collect complete: tokens={Tokens} dollars={Dollars} unpriced={Unpriced}",
                summary.Tokens, summary.Dollars, summary.HasUnpriced);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _health.Fail(exception.Message);
            _logger.LogError(exception, "Codex collection failed; the service will retry on the next interval.");
        }
    }
}
