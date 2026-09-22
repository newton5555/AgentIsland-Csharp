using AgentIsland.Core.Agents;
using AgentIsland.Providers.Cost.Codex;
using AgentMonitoring.Activity;
using AgentMonitoring.Accounts;
using AgentMonitoring.Balances;
using AgentMonitoring.Consumption;
using AgentMonitoring.Enablement;
using AgentMonitoring.Notifications;
using AgentMonitoring.Pricing;
using AgentMonitoring.Queries;
using AgentMonitoring.Quotas;
using AgentMonitoring.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentMonitoring.Host;

public static class HeadlessServices
{
    public static IServiceCollection AddHeadlessMonitoring(
        this IServiceCollection services,
        HeadlessOptions? options = null)
    {
        options ??= HeadlessOptions.FromEnvironment();
        services.AddSingleton(options);
        services.AddSingleton<IAgentEnablement>(AlwaysEnabledAgents.Instance);
        services.AddSingleton(sp => new ConsumptionStore(options.ConsumptionStorePath));
        services.AddSingleton<IConsumptionStore>(sp => sp.GetRequiredService<ConsumptionStore>());
        services.AddSingleton<IConsumptionQuery>(sp => sp.GetRequiredService<ConsumptionStore>());
        services.AddSingleton<IPricer, SnapshotPricer>();
        services.AddSingleton<IConsumptionCollector>(sp =>
            new CodexConsumptionCollector(
                sp.GetRequiredService<IConsumptionStore>(),
                () => CodexRolloutDiscovery.FromHomes(options.CodexHomes)));
        services.AddSingleton<ILedgerSnapshotStore, LedgerSnapshotStore>();
        services.AddSingleton<IQuotaStore, QuotaStore>();
        services.AddSingleton<IBalanceStore, BalanceStore>();
        services.AddSingleton<IAccountDirectory, MemoryAccountDirectory>();
        services.AddSingleton<IActivitySnapshotStore, ActivitySnapshotStore>();
        services.AddSingleton<ReminderBroker>();
        services.AddSingleton<IMonitoringQuery, MonitoringQueryService>();
        services.AddSingleton(sp => new AgentRuntime(
            sp.GetRequiredService<ILedgerSnapshotStore>(),
            sp.GetRequiredService<IAgentEnablement>(),
            Array.Empty<IAgentProvider>(),
            sp.GetRequiredService<IConsumptionCollector>()));
        return services;
    }

    public static IHostBuilder CreateHostBuilder(string[]? args = null) =>
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args ?? Array.Empty<string>())
            .ConfigureServices(services =>
            {
                services.AddHeadlessMonitoring();
                services.AddHostedService<HeadlessCollectWorker>();
            });
}

public sealed class HeadlessOptions
{
    public IReadOnlyList<string> CodexHomes { get; init; } = Array.Empty<string>();
    public string? ConsumptionStorePath { get; init; }
    public TimeSpan CollectInterval { get; init; } = TimeSpan.FromMinutes(5);

    public static HeadlessOptions FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        IReadOnlyList<string> homes = string.IsNullOrWhiteSpace(env)
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") }
            : env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentIsland", "cache", "codex-consumption.v1.json");
        return new HeadlessOptions { CodexHomes = homes, ConsumptionStorePath = cache };
    }
}
