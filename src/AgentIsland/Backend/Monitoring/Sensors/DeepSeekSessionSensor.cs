using AgentIsland.Core;
using AgentIsland.Core.Agents;

namespace AgentIsland.Backend.Monitoring.Sensors;

public sealed class DeepSeekSessionSensor : ISessionSensor
{
    public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<ScannedSession>>(Scan(now, lastWorking));
    }

    public static List<ScannedSession> Scan(
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateTimeOffset> lastWorking)
    {
        return DeepSeekActivityReader.Scan(now, lastWorking);
    }
}
