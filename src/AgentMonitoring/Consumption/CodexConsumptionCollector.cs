using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.Codex;

namespace AgentMonitoring.Consumption;

/// Incremental Codex collect: byte cursors, replay/fork filter, record-id
/// dedup. Consumer cancellation only drops the wait.
public sealed class CodexConsumptionCollector : IConsumptionCollector
{
    private readonly IConsumptionStore _store;
    private readonly Func<IReadOnlyList<CodexRolloutFile>> _discover;
    private readonly object _gate = new();
    private Task? _inFlight;
    private CancellationTokenSource? _cts;
    private long _generation;

    public CodexConsumptionCollector(
        IConsumptionStore store,
        Func<IReadOnlyList<CodexRolloutFile>> discover)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
    }

    public Task CollectAsync(AgentKey agent, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(agent.Value, AgentKeys.Codex.Value, StringComparison.Ordinal))
            return Task.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        Task shared;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } existing)
            {
                shared = existing;
            }
            else
            {
                _cts?.Dispose();
                var cts = new CancellationTokenSource();
                var generation = _generation;
                shared = Task.Run(() => CollectCore(generation, cts.Token), CancellationToken.None);
                _cts = cts;
                _inFlight = shared;
                _ = Observe(shared, cts);
            }
        }

        return cancellationToken.CanBeCanceled
            ? shared.WaitAsync(cancellationToken)
            : shared;
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _cts?.Cancel();
            _cts = null;
            _inFlight = null;
        }
    }

    private async Task Observe(Task task, CancellationTokenSource cts)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, task))
                {
                    _inFlight = null;
                    _cts = null;
                }
            }

            cts.Dispose();
        }
    }

    private void CollectCore(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = _discover();
        var incoming = new List<CodexParsedTurn>();
        var fileStates = new Dictionary<string, CodexFileState>(StringComparer.OrdinalIgnoreCase);
        var replaceSources = new List<string>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(file.FullPath); }
            catch { continue; }
            if (!info.Exists) continue;

            var size = info.Length;
            var mtime = info.LastWriteTimeUtc.Ticks;
            var existing = _store.GetFileState(file.FullPath);
            var rebuild = existing is null
                || existing.Cursor.ParserVersion != CodexRolloutDiscovery.ParserVersion
                || size < existing.Cursor.Position
                || (size == existing.Cursor.Position
                    && mtime != existing.Cursor.MtimeUtcTicks);

            if (!rebuild && size == existing!.Cursor.Position)
            {
                fileStates[file.FullPath] = existing;
                continue;
            }

            var start = 0L;
            var parseState = new CodexParseState();
            if (!rebuild && existing is not null)
            {
                start = existing.Cursor.Position;
                parseState = FromFileState(existing);
            }

            var (turns, nextState, nextOffset, ok) = CodexTranscriptParser.ParseFromOffset(
                file.FullPath, parseState, start);
            if (!ok)
            {
                if (existing is not null) fileStates[file.FullPath] = existing;
                continue;
            }

            if (rebuild) replaceSources.Add(file.FullPath);
            incoming.AddRange(turns);
            fileStates[file.FullPath] = ToFileState(file.FullPath, nextState, nextOffset, size, mtime);
        }

        if (Volatile.Read(ref _generation) != generation) return;

        var kept = CodexReplayPlan.Apply(incoming, ParentBaselines(_store.ReadFileStates()));
        var replacing = new HashSet<string>(replaceSources, StringComparer.OrdinalIgnoreCase);
        var facts = new List<ConsumptionFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in kept)
        {
            if (!seen.Add(turn.Fact.Source.RecordId)) continue;
            var path = turn.Fact.Source.SourcePath;
            var replacingThis = path is not null && replacing.Contains(path);
            if (!replacingThis && _store.ContainsRecord(turn.Fact.Source.RecordId)) continue;
            facts.Add(turn.Fact);
        }

        if (Volatile.Read(ref _generation) != generation) return;
        _store.Commit(AgentKeys.Codex, facts, fileStates, replaceSources);
    }

    private static Dictionary<string, (long Input, long Output)> ParentBaselines(
        IReadOnlyList<CodexFileState> states)
    {
        var totals = new Dictionary<string, (long Input, long Output)>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (state.SessionId is not { Length: > 0 } session) continue;
            if (!string.IsNullOrEmpty(state.ForkedFromId)) continue;
            if (state.LastTotalInput is not { } input || state.LastTotalOutput is not { } output) continue;
            if (!totals.TryGetValue(session, out var current)
                || input > current.Input
                || output > current.Output)
            {
                totals[session] = (input, output);
            }
        }

        return totals;
    }

    private static CodexParseState FromFileState(CodexFileState state) => new()
    {
        SessionId = state.SessionId,
        ForkedFromId = state.ForkedFromId,
        SessionStartedAt = state.SessionStartedAt,
        ProjectId = state.ProjectId,
        CurrentModel = state.CurrentModel,
        ModelIsFallback = state.ModelIsFallback,
        ServiceTier = state.ServiceTier,
        LastTotalInput = state.LastTotalInput,
        LastTotalOutput = state.LastTotalOutput,
    };

    private static CodexFileState ToFileState(
        string path,
        CodexParseState state,
        long position,
        long size,
        long mtime) =>
        new(
            new ScanCursor(
                AgentKeys.Codex,
                path,
                position,
                CodexRolloutDiscovery.ParserVersion,
                size,
                mtime,
                null),
            state.SessionId,
            state.ForkedFromId,
            state.SessionStartedAt,
            state.ProjectId,
            state.CurrentModel,
            state.ModelIsFallback,
            state.ServiceTier,
            state.LastTotalInput,
            state.LastTotalOutput);
}
