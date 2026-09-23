using AgentIsland.Core.Agents;
using AgentMonitoring.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentMonitoring.Host;

public static class MonitoringApiEndpoints
{
    private static readonly TimeSpan MaximumQueryRange = TimeSpan.FromDays(3660);

    public static WebApplication MapMonitoringApi(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", (HeadlessCollectorHealth health) =>
        {
            var collector = health.Read();
            return collector.Running
                ? Results.Ok(new { status = "ready", collector = collector.Collector })
                : Results.Json(new { status = "starting" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.MapGet("/api/v1/health", (HeadlessCollectorHealth health) => Results.Ok(health.Read()));
        app.MapGet("/api/v1/agents", (IMonitoringQuery query) =>
            Results.Ok(query.GetOverviews().Select(MonitoringApiMapper.ToApiV1).ToList()));
        app.MapGet("/api/v1/agents/{agentKey}/overview", (string agentKey, IMonitoringQuery query) =>
        {
            if (!TryAgentKey(agentKey, out var agent))
                return Results.BadRequest(new { error = "agentKey is invalid." });

            var known = query.GetOverviews();
            if (!known.Any(overview => overview.Agent == agent))
                return Results.NotFound(new { error = "Agent has no monitoring data in this service." });
            return Results.Ok(MonitoringApiMapper.ToApiV1(query.GetOverview(agent)));
        });
        app.MapGet("/api/v1/consumption", (
            DateTimeOffset? from,
            DateTimeOffset? to,
            string[]? agent,
            IMonitoringQuery query) =>
        {
            if (!TryRange(from, to, out var error))
                return Results.BadRequest(new { error });
            if (!TryAgents(agent, out var agents, out error))
                return Results.BadRequest(new { error });

            return Results.Ok(MonitoringApiMapper.ToApiV1(
                query.GetConsumptionSummary(from!.Value, to!.Value, agents)));
        });
        app.MapGet("/api/v1/reports", (
            DateTimeOffset? from,
            DateTimeOffset? to,
            string[]? agent,
            IMonitoringQuery query) =>
        {
            if (!TryRange(from, to, out var error))
                return Results.BadRequest(new { error });
            if (!TryAgents(agent, out var agents, out error))
                return Results.BadRequest(new { error });

            return Results.Ok(MonitoringApiMapper.ToApiV1(
                query.GetReport(from!.Value, to!.Value, agents)));
        });

        return app;
    }

    private static bool TryRange(DateTimeOffset? from, DateTimeOffset? to, out string? error)
    {
        if (from is null || to is null)
        {
            error = "Both 'from' and 'to' date-time query parameters are required.";
            return false;
        }

        if (to.Value <= from.Value)
        {
            error = "'to' must be later than 'from'.";
            return false;
        }

        if (to.Value - from.Value > MaximumQueryRange)
        {
            error = "The requested range cannot exceed ten years.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryAgents(
        IReadOnlyList<string>? values,
        out IReadOnlyList<AgentKey>? agents,
        out string? error)
    {
        agents = null;
        if (values is null || values.Count == 0)
        {
            error = null;
            return true;
        }

        if (values.Count > 32)
        {
            error = "At most 32 agent filters may be requested.";
            return false;
        }

        try
        {
            agents = values.Select(value => new AgentKey(value)).Distinct().ToList();
            error = null;
            return true;
        }
        catch (ArgumentException)
        {
            error = "Each agent filter must be a non-empty agent key.";
            return false;
        }
    }

    private static bool TryAgentKey(string value, out AgentKey agent)
    {
        try
        {
            agent = new AgentKey(value);
            return true;
        }
        catch (ArgumentException)
        {
            agent = default;
            return false;
        }
    }
}
