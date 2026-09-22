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
    private readonly ILogger<HeadlessCollectWorker> _logger;

    public HeadlessCollectWorker(
        IConsumptionCollector collector,
        IMonitoringQuery query,
        HeadlessOptions options,
        ILogger<HeadlessCollectWorker> logger)
    {
        _collector = collector;
        _query = query;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CollectOnce(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_options.CollectInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await CollectOnce(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CollectOnce(CancellationToken cancellationToken)
    {
        await _collector.CollectAsync(AgentKeys.Codex, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
        var summary = _query.GetConsumptionSummary(start, now.AddDays(1));
        _logger.LogInformation(
            "Codex collect complete: tokens={Tokens} dollars={Dollars} unpriced={Unpriced}",
            summary.Tokens, summary.Dollars, summary.HasUnpriced);
    }
}
