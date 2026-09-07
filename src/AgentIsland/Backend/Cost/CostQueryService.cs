using AgentIsland.Core.Cost;
using AgentIsland.Core.Agents;
using AgentIsland.Backend.Settings;
using AgentIsland.UI.Providers;

namespace AgentIsland.Backend.Cost;

/// The one production cost-read entry point. CostStore and report pages share
/// an in-flight read for the same provider, while a cancelled report remains
/// only a cancelled consumer and does not tear down another consumer's scan.
/// Parsed events are kept in the result only until its callers finish; the
/// long-lived per-file memo is still LogParseCache.
public sealed record CostScanResult(
    DisplayProvider Provider,
    long ProviderVersion,
    DateTimeOffset ScannedAt,
    IReadOnlyList<TokenEvent> Events,
    ProviderCostSummary Summary);

public sealed class CostQueryService : ICostQueryService
{
    private readonly IProviderVisibilityStore _visibilityStore;
    private readonly IReadOnlyDictionary<DisplayProvider, ICostLedgerReader> _readers;

    private sealed class ProviderState
    {
        public long Version;
        public Task<CostScanResult>? ScanTask;
        public CancellationTokenSource? ScanCts;
    }

    private readonly object _gate = new();
    private readonly Dictionary<DisplayProvider, ProviderState> _states =
        new();

    public CostQueryService(
        IProviderVisibilityStore visibilityStore,
        IEnumerable<IAgentProvider>? providers = null)
    {
        _visibilityStore = visibilityStore ?? throw new ArgumentNullException(nameof(visibilityStore));
        _readers = (providers ?? Array.Empty<IAgentProvider>())
            .Where(provider => provider.CostLedgerReader is not null)
            .Select(provider => (Provider: DisplayProviders.Parse(provider.Descriptor.Key.Value), Reader: provider.CostLedgerReader!))
            .Where(pair => pair.Provider is not null)
            .ToDictionary(pair => pair.Provider!.Value, pair => pair.Reader);
    }

    /// Starts or joins the current provider scan. `consumerCancellation` only
    /// cancels the caller's wait; provider invalidation is what cancels the
    /// shared work itself.
    public Task<CostScanResult> ScanAsync(
        DisplayProvider provider,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default)
    {
        if (consumerCancellation.IsCancellationRequested)
        {
            return Task.FromCanceled<CostScanResult>(consumerCancellation);
        }

        Task<CostScanResult> shared;
        lock (_gate)
        {
            // This check belongs before task creation. ReportPeriods captures
            // Enabled before entering Task.Run, so a toggle in between must
            // never warm a disabled provider's reader/cache.
            if (!_visibilityStore.IsEnabled(provider))
            {
                return Task.FromException<CostScanResult>(
                    new ProviderDisabledException(provider));
            }
            if (consumerCancellation.IsCancellationRequested)
            {
                return Task.FromCanceled<CostScanResult>(consumerCancellation);
            }
            var state = StateFor(provider);
            if (state.ScanTask is { IsCompleted: false } existing)
            {
                shared = existing;
            }
            else
            {
                state.ScanCts?.Dispose();
                var cts = new CancellationTokenSource();
                var version = state.Version;
                shared = Task.Run(
                    () => ScanCoreAsync(provider, version, lookbackDays, now, cts.Token),
                    CancellationToken.None);
                state.ScanCts = cts;
                state.ScanTask = shared;
                _ = ObserveCompletion(provider, state, shared, cts);
            }
        }

        return consumerCancellation.CanBeCanceled
            ? shared.WaitAsync(consumerCancellation)
            : shared;
    }

    public sealed class ProviderDisabledException : InvalidOperationException
    {
        public ProviderDisabledException(DisplayProvider provider)
            : base($"Cost scan skipped because {provider} is disabled.")
        {
            Provider = provider;
        }

        public DisplayProvider Provider { get; }
    }

    /// Convenience entry point used by diagnostics and performance sampling
    /// to measure the same path as the live cost poll.
    public Task<CostScanResult> ScanCurrentAsync(
        DisplayProvider provider,
        CancellationToken consumerCancellation = default) =>
        ScanAsync(
            provider,
            CostSummarizer.YearHistoryDays(DateTimeOffset.Now),
            DateTimeOffset.Now,
            consumerCancellation);

    public Task<CostScanResult> ScanCurrentAsync(
        DisplayProvider provider,
        DateTimeOffset now,
        CancellationToken consumerCancellation = default) =>
        ScanAsync(
            provider,
            CostSummarizer.YearHistoryDays(now),
            now,
            consumerCancellation);

    /// Invalidates only one provider. A disabled provider cannot publish its
    /// old result, and a later re-enable receives a new generation.
    public void Invalidate(DisplayProvider provider)
    {
        lock (_gate)
        {
            var state = StateFor(provider);
            state.Version++;
            state.ScanCts?.Cancel();
            state.ScanCts = null;
            state.ScanTask = null;
        }
    }

    public long Version(DisplayProvider provider)
    {
        lock (_gate) return StateFor(provider).Version;
    }

    public bool IsCurrent(DisplayProvider provider, long version)
    {
        lock (_gate) return StateFor(provider).Version == version;
    }

    private ProviderState StateFor(DisplayProvider provider)
    {
        if (_states.TryGetValue(provider, out var state)) return state;
        state = new ProviderState();
        _states[provider] = state;
        return state;
    }

    private async Task ObserveCompletion(
        DisplayProvider provider,
        ProviderState state,
        Task<CostScanResult> task,
        CancellationTokenSource cts)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(state.ScanTask, task))
                {
                    state.ScanTask = null;
                    state.ScanCts = null;
                }
            }
            cts.Dispose();
        }
    }

    private async Task<CostScanResult> ScanCoreAsync(
        DisplayProvider provider,
        long providerVersion,
        int lookbackDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TokenEvent> events;
        if (_readers.TryGetValue(provider, out var reader))
        {
            events = await reader.ReadCostEventsAsync(lookbackDays, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            events = provider switch
            {
                DisplayProvider.Claude => ClaudeLogReader.Scan(lookbackDays, cancellationToken),
                DisplayProvider.Codex => CodexLogReader.Scan(lookbackDays, cancellationToken),
                DisplayProvider.Antigravity => AntigravityLogReader.Scan(lookbackDays, cancellationToken),
                DisplayProvider.Grok => GrokLogReader.Scan(lookbackDays, cancellationToken),
                DisplayProvider.Cursor => CursorLogReader.Scan(lookbackDays, cancellationToken),
                DisplayProvider.DeepSeek => DeepSeekLogReader.Scan(lookbackDays, cancellationToken),
                _ => Array.Empty<TokenEvent>(),
            };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new CostScanResult(
            provider,
            providerVersion,
            now,
            events,
            CostSummarizer.Summarize(events, now));
    }
}
