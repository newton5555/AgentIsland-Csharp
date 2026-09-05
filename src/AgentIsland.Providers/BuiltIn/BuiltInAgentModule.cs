using AgentIsland.Core.Agents;

namespace AgentIsland.Providers.BuiltIn;

public sealed record BuiltInAgentModule(
    AgentDescriptor Descriptor,
    ISessionSensor? SessionSensor = null,
    IUsageFetcher? UsageFetcher = null,
    ICostLedgerReader? CostLedgerReader = null,
    ISessionLauncher? SessionLauncher = null,
    IReauthHandler? ReauthHandler = null) : IAgentProvider;

