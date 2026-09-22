using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;

namespace AgentMonitoring.Quotas;

/// Per-account quota refresh. A failed fetch keeps the last successful
/// reading. Consumer cancellation only drops the wait; Invalidate bumps the
/// account generation so a late result cannot land on a newer identity.
public sealed class QuotaRefresher : IQuotaRefresher
{
    private readonly IQuotaStore _store;
    private readonly IReadOnlyDictionary<AgentKey, IQuotaSource> _sources;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private sealed class Slot
    {
        public long Generation;
        public Task<QuotaSnapshot>? Task;
        public CancellationTokenSource? Cts;
    }

    public QuotaRefresher(IQuotaStore store, IEnumerable<IQuotaSource> sources)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sources = (sources ?? Array.Empty<IQuotaSource>()).ToDictionary(source => source.Agent);
    }

    public Task<QuotaSnapshot> RefreshAsync(AccountRef account, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<QuotaSnapshot>(cancellationToken);

        Task<QuotaSnapshot> shared;
        lock (_gate)
        {
            var slot = SlotFor(account);
            if (slot.Task is { IsCompleted: false } existing)
            {
                shared = existing;
            }
            else
            {
                slot.Cts?.Dispose();
                var cts = new CancellationTokenSource();
                var generation = slot.Generation;
                shared = Task.Run(() => RefreshCore(account, generation, cts.Token), CancellationToken.None);
                slot.Cts = cts;
                slot.Task = shared;
                _ = Observe(slot, shared, cts);
            }
        }

        return cancellationToken.CanBeCanceled
            ? shared.WaitAsync(cancellationToken)
            : shared;
    }

    public void Invalidate(AccountRef account)
    {
        lock (_gate)
        {
            var slot = SlotFor(account);
            slot.Generation++;
            slot.Cts?.Cancel();
            slot.Cts = null;
            slot.Task = null;
        }
    }

    private Slot SlotFor(AccountRef account)
    {
        var key = QuotaStore.Key(account);
        if (_slots.TryGetValue(key, out var slot)) return slot;
        slot = new Slot();
        _slots[key] = slot;
        return slot;
    }

    private async Task Observe(Slot slot, Task<QuotaSnapshot> task, CancellationTokenSource cts)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(slot.Task, task))
                {
                    slot.Task = null;
                    slot.Cts = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task<QuotaSnapshot> RefreshCore(
        AccountRef account,
        long generation,
        CancellationToken cancellationToken)
    {
        if (!_sources.TryGetValue(account.Agent, out var source))
        {
            return Commit(account, AppUsage.ErrorPair("no quota source"), generation);
        }

        AppUsage fetched;
        try
        {
            fetched = await source.FetchAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            fetched = AppUsage.ErrorPair(error.Message);
        }

        return Commit(account, fetched, generation);
    }

    private QuotaSnapshot Commit(AccountRef account, AppUsage fetched, long generation)
    {
        lock (_gate)
        {
            if (SlotFor(account).Generation != generation)
            {
                return _store.Read(account)
                    ?? new QuotaSnapshot(account, fetched, DateTimeOffset.UtcNow, null, fetched.FiveHour.Error);
            }

            var previous = _store.Read(account);
            var merged = QuotaMerge.Apply(previous?.Usage, fetched);
            var allFailed = QuotaMerge.IsErrorOnly(fetched);
            var windowError =
                QuotaMerge.IsFailedWindow(fetched.FiveHour) ? fetched.FiveHour.Error
                : QuotaMerge.IsFailedWindow(fetched.Weekly) ? fetched.Weekly.Error
                : null;
            var now = DateTimeOffset.UtcNow;
            var snapshot = new QuotaSnapshot(
                account,
                merged,
                now,
                allFailed ? previous?.SucceededAt : now,
                allFailed ? windowError : null);
            _store.Commit(snapshot);
            return snapshot;
        }
    }
}
