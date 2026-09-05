using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;

namespace AgentIsland.Core.Agents;

/// <summary>
/// Sensor capability: scans local machine / sessions to discover agent activities.
/// </summary>
public interface ISessionSensor
{
    ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default);
}

/// <summary>
/// Usage capability: fetches official quota windows, percentage limits, or balances.
/// </summary>
public interface IUsageFetcher
{
    ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default);
}

/// <summary>
/// Cost capability: parses local token logs and computes spend events.
/// </summary>
public interface ICostLedgerReader
{
    ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(CancellationToken ct = default);
}

/// <summary>
/// Navigation capability: launches or activates an agent CLI/desktop session.
/// </summary>
public interface ISessionLauncher
{
    bool LaunchSession(ScannedSession session);
}

/// <summary>
/// Reauthentication capability: triggers OAuth / login flow.
/// </summary>
public interface IReauthHandler
{
    Task StartReauthAsync(CancellationToken ct = default);
}

/// <summary>
/// Core contract for an extensible Agent Provider.
/// An agent provider declares its descriptor and can optionally implement
/// one or more capability interfaces.
/// </summary>
public interface IAgentProvider : IAgentModule
{
    ISessionSensor? SessionSensor => this as ISessionSensor;
    IUsageFetcher? UsageFetcher => this as IUsageFetcher;
    ICostLedgerReader? CostLedgerReader => this as ICostLedgerReader;
    ISessionLauncher? SessionLauncher => this as ISessionLauncher;
    IReauthHandler? ReauthHandler => this as IReauthHandler;
}
