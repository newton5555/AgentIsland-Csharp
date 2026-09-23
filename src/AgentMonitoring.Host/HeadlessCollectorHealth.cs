namespace AgentMonitoring.Host;

public sealed record HeadlessCollectorHealthSnapshot(
    string Collector,
    bool Running,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    int ConsecutiveFailures);

/// Thread-safe operational state exposed by the local health endpoint.
public sealed class HeadlessCollectorHealth
{
    private readonly object _gate = new();
    private HeadlessCollectorHealthSnapshot _snapshot = new(
        "codex-consumption", false, null, null, null, 0);

    public HeadlessCollectorHealthSnapshot Read()
    {
        lock (_gate) return _snapshot;
    }

    public void Start()
    {
        lock (_gate) _snapshot = _snapshot with { Running = true };
    }

    public void BeginAttempt(DateTimeOffset at)
    {
        lock (_gate) _snapshot = _snapshot with { LastAttemptAt = at };
    }

    public void Succeed(DateTimeOffset at)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastSuccessAt = at,
                LastError = null,
                ConsecutiveFailures = 0,
            };
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastError = error,
                ConsecutiveFailures = _snapshot.ConsecutiveFailures + 1,
            };
        }
    }

    public void Stop()
    {
        lock (_gate) _snapshot = _snapshot with { Running = false };
    }
}
