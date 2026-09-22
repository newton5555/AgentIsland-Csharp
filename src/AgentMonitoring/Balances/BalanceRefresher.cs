using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;
using AgentMonitoring.Quotas;

namespace AgentMonitoring.Balances;

/// Per-account balance refresh. A failed fetch keeps the last successful
/// amount and records the error.
public sealed class BalanceRefresher : IBalanceRefresher
{
    private readonly IBalanceStore _store;
    private readonly IReadOnlyDictionary<AgentKey, IBalanceSource> _sources;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private sealed class Slot
    {
        public long Generation;
        public Task<RemoteBalanceSnapshot>? Task;
        public CancellationTokenSource? Cts;
    }

    public BalanceRefresher(IBalanceStore store, IEnumerable<IBalanceSource> sources)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sources = (sources ?? Array.Empty<IBalanceSource>()).ToDictionary(source => source.Agent);
    }

    public Task<RemoteBalanceSnapshot> RefreshAsync(AccountRef account, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<RemoteBalanceSnapshot>(cancellationToken);

        Task<RemoteBalanceSnapshot> shared;
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

    private async Task Observe(Slot slot, Task<RemoteBalanceSnapshot> task, CancellationTokenSource cts)
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

    private async Task<RemoteBalanceSnapshot> RefreshCore(
        AccountRef account,
        long generation,
        CancellationToken cancellationToken)
    {
        BalanceFetchResult result;
        if (!_sources.TryGetValue(account.Agent, out var source))
        {
            result = new BalanceFetchResult.Failed("no balance source");
        }
        else
        {
            try
            {
                result = await source.FetchAsync(account, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                result = new BalanceFetchResult.Failed(error.Message);
            }
        }

        return Commit(account, result, generation);
    }

    private RemoteBalanceSnapshot Commit(AccountRef account, BalanceFetchResult result, long generation)
    {
        lock (_gate)
        {
            var previous = _store.Read(account);
            if (SlotFor(account).Generation != generation)
            {
                return previous ?? Empty(account, "superseded");
            }

            var now = DateTimeOffset.UtcNow;
            RemoteBalanceSnapshot snapshot = result switch
            {
                BalanceFetchResult.Success success => new RemoteBalanceSnapshot(
                    account,
                    success.Balance.Currency,
                    success.Balance.Amount,
                    now,
                    now,
                    null,
                    success.IsAvailable),
                BalanceFetchResult.Failed failed when previous is { SucceededAt: not null } keep =>
                    keep with { FetchedAt = now, Error = failed.Error },
                BalanceFetchResult.Failed failed => Empty(account, failed.Error),
                _ => Empty(account, "unknown balance result"),
            };
            _store.Commit(snapshot);
            return snapshot;
        }
    }

    private static RemoteBalanceSnapshot Empty(AccountRef account, string error) =>
        new(account, "", 0, DateTimeOffset.UtcNow, null, error, false);
}
