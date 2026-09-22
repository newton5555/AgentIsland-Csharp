using AgentIsland.Core.Agents;
using AgentMonitoring.Consumption;
using AgentMonitoring.Host;
using AgentMonitoring.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Any(arg => string.Equals(arg, "--once", StringComparison.OrdinalIgnoreCase)))
{
    var services = new ServiceCollection();
    services.AddHeadlessMonitoring();
    using var provider = services.BuildServiceProvider();
    var collector = provider.GetRequiredService<IConsumptionCollector>();
    var query = provider.GetRequiredService<IMonitoringQuery>();
    await collector.CollectAsync(AgentKeys.Codex);
    var now = DateTimeOffset.Now;
    var start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);
    var summary = query.GetConsumptionSummary(start, now.AddDays(1));
    Console.WriteLine($"tokens={summary.Tokens} dollars={summary.Dollars:0.###} agents={summary.ByAgent.Count}");
    return;
}

await HeadlessServices.CreateHostBuilder(args).Build().RunAsync();
