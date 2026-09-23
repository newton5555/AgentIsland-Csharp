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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentMonitoring.Host;

public static class HeadlessServices
{
    public static IServiceCollection AddHeadlessMonitoring(
        this IServiceCollection services,
        HeadlessOptions? options = null)
    {
        options ??= HeadlessOptions.FromEnvironment();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<HeadlessCollectorHealth>();
        services.AddSingleton<IAgentEnablement>(AlwaysEnabledAgents.Instance);
        services.AddSingleton(sp => new ConsumptionStore(options.ConsumptionStorePath, strictPersistence: true));
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

    public static WebApplication CreateWebApplication(
        HeadlessOptions? options = null)
    {
        options ??= HeadlessOptions.FromEnvironment();
        options.Validate();

        // Bind explicitly to loopback. Command-line and ASPNETCORE_URLS
        // settings must not accidentally expose this local-data API to LAN.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
        });
        builder.Services.AddHeadlessMonitoring(options);
        builder.Services.AddHostedService<HeadlessCollectWorker>();
        builder.Services.AddProblemDetails();
        if (options.AllowedOrigins.Count > 0)
        {
            builder.Services.AddCors(cors => cors.AddPolicy("local-frontends", policy =>
                policy.WithOrigins(options.AllowedOrigins.ToArray())
                    .AllowAnyHeader()
                    .AllowAnyMethod()));
        }

        builder.WebHost.ConfigureKestrel(server =>
            server.ListenLocalhost(options.ApiPort, listen => listen.Protocols = HttpProtocols.Http1));

        var app = builder.Build();
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var host = context.Request.Host.Host;
            if (!IsLoopbackHost(host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Only loopback Host headers are accepted." });
                return;
            }

            await next();
        });
        if (options.AllowedOrigins.Count > 0)
            app.UseCors("local-frontends");
        app.MapMonitoringApi();
        app.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.Register(() =>
                app.Logger.LogInformation("Local monitoring API listening on loopback port {Port}", options.ApiPort));
        return app;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address);
}

public sealed class HeadlessOptions
{
    public IReadOnlyList<string> CodexHomes { get; init; } = Array.Empty<string>();
    public string? ConsumptionStorePath { get; init; }
    public TimeSpan CollectInterval { get; init; } = TimeSpan.FromMinutes(5);
    public int ApiPort { get; init; } = 43127;
    public IReadOnlyList<string> AllowedOrigins { get; init; } = Array.Empty<string>();

    public void Validate()
    {
        if (CollectInterval <= TimeSpan.Zero || CollectInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(CollectInterval), "Collection interval must be greater than zero and no more than one day.");
        if (ApiPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(ApiPort), "The local API port must be between 1 and 65535.");
        foreach (var origin in AllowedOrigins)
        {
            if (!IsAllowedLocalOrigin(origin))
                throw new ArgumentException($"CORS origin must be an exact HTTP(S) loopback origin: {origin}", nameof(AllowedOrigins));
        }
    }

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
            "AgentIsland", "cache", "codex-consumption.headless.v1.json");
        var portText = Environment.GetEnvironmentVariable("AGENT_MONITORING_PORT");
        var port = string.IsNullOrWhiteSpace(portText)
            ? 43127
            : int.TryParse(portText, out var parsedPort) && parsedPort is >= 1 and <= 65535
                ? parsedPort
                : throw new InvalidOperationException("AGENT_MONITORING_PORT must be an integer between 1 and 65535.");
        var allowedOrigins = ParseAllowedOrigins(Environment.GetEnvironmentVariable("AGENT_MONITORING_ALLOWED_ORIGINS"));
        var intervalText = Environment.GetEnvironmentVariable("AGENT_MONITORING_COLLECT_INTERVAL_SECONDS");
        var interval = string.IsNullOrWhiteSpace(intervalText)
            ? TimeSpan.FromMinutes(5)
            : int.TryParse(intervalText, out var seconds) && seconds is > 0 and <= 86400
                ? TimeSpan.FromSeconds(seconds)
                : throw new InvalidOperationException("AGENT_MONITORING_COLLECT_INTERVAL_SECONDS must be between 1 and 86400.");
        return new HeadlessOptions
        {
            CodexHomes = homes,
            ConsumptionStorePath = cache,
            ApiPort = port,
            AllowedOrigins = allowedOrigins,
            CollectInterval = interval,
        };
    }

    private static IReadOnlyList<string> ParseAllowedOrigins(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(origin => Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                ? parsed.GetLeftPart(UriPartial.Authority)
                : origin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsAllowedLocalOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)) return false;
        if ((origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps) || origin.UserInfo.Length > 0
            || origin.Query.Length > 0 || origin.Fragment.Length > 0
            || origin.AbsolutePath != "/") return false;
        var canonical = origin.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(value, canonical, StringComparison.Ordinal)) return false;
        var host = origin.Host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }
}
