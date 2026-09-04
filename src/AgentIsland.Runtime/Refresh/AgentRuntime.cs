using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Runtime.Snapshots;

namespace AgentIsland.Runtime.Refresh;

/// Coordinates provider snapshots for any desktop front end. The coordinator
/// owns no Dispatcher and has no UI-thread assumptions; hosts subscribe to the
/// event and marshal it to their own UI thread.
public sealed class AgentRuntime
{
    private readonly IReadOnlyList<IAgentSnapshotSource> _sources;
    private readonly object _gate = new();
    private IReadOnlyDictionary<AgentKey, AgentSnapshot> _snapshots =
        new Dictionary<AgentKey, AgentSnapshot>();
    private int _refreshInFlight;

    public AgentRuntime(IEnumerable<IAgentSnapshotSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources
            .Where(source => source is not null)
            .GroupBy(source => source.Agent)
            .Select(group => group.First())
            .ToArray();
    }

    public event EventHandler<AgentSnapshotsChangedEventArgs>? SnapshotsChanged;

    public IReadOnlyDictionary<AgentKey, AgentSnapshot> Snapshots
    {
        get
        {
            lock (_gate) return _snapshots;
        }
    }

    /// Refreshes all sources concurrently. A second call while a refresh is
    /// running is coalesced; the next timer tick can request another refresh.
    /// A failed source preserves its previous value as stale data and never
    /// prevents other providers from publishing.
    public async Task<bool> RefreshAsync(
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _refreshInFlight, 1) != 0) return false;

        var now = observedAt ?? DateTimeOffset.Now;
        try
        {
            IReadOnlyDictionary<AgentKey, AgentSnapshot> previous;
            lock (_gate) previous = _snapshots;

            var reads = _sources.Select(source => ReadSafeAsync(source, now, previous, cancellationToken));
            var next = await Task.WhenAll(reads).ConfigureAwait(false);
            var published = next.ToDictionary(snapshot => snapshot.Agent);

            lock (_gate) _snapshots = published;
            SnapshotsChanged?.Invoke(this,
                new AgentSnapshotsChangedEventArgs(published, now));
            return true;
        }
        finally
        {
            Volatile.Write(ref _refreshInFlight, 0);
        }
    }

    /// Runs the normal polling cadence without creating a UI timer. The
    /// caller owns the cancellation token and can stop cleanly on app exit.
    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        using var timer = new PeriodicTimer(interval);
        await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AgentSnapshot> ReadSafeAsync(
        IAgentSnapshotSource source,
        DateTimeOffset observedAt,
        IReadOnlyDictionary<AgentKey, AgentSnapshot> previous,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await source.ReadAsync(observedAt, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Agent != source.Agent)
            {
                return AgentSnapshot.ErrorState(
                    source.Agent,
                    ActivityState.Idle,
                    observedAt,
                    $"snapshot agent mismatch: {snapshot.Agent}",
                    previous.GetValueOrDefault(source.Agent));
            }
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return AgentSnapshot.ErrorState(
                source.Agent,
                ActivityState.Idle,
                observedAt,
                error.Message,
                previous.GetValueOrDefault(source.Agent));
        }
    }
}
