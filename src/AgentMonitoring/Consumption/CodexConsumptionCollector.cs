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

        var replacing = new HashSet<string>(replaceSources, StringComparer.OrdinalIgnoreCase);
        var statesByPath = new Dictionary<string, CodexFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in _store.ReadFileStates())
        {
            if (!replacing.Contains(state.Cursor.SourceIdentity))
                statesByPath[state.Cursor.SourceIdentity] = state;
        }
        foreach (var (path, state) in fileStates)
            statesByPath[path] = state;

        var parentSessions = statesByPath.Values
            .Where(state => string.IsNullOrEmpty(state.ForkedFromId) && !string.IsNullOrEmpty(state.SessionId))
            .Select(state => state.SessionId!)
            .ToHashSet(StringComparer.Ordinal);
        var parentHistory = ParentHistory(incoming, replacing, parentSessions);
        var kept = CodexReplayPlan.Apply(incoming, parentHistory);
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

    private IReadOnlyDictionary<string, IReadOnlyList<CodexUsageCheckpoint>> ParentHistory(
        IReadOnlyList<CodexParsedTurn> incoming,
        IReadOnlySet<string> replacing,
        IReadOnlySet<string> parentSessions)
    {
        var history = new Dictionary<string, List<CodexUsageCheckpoint>>(StringComparer.Ordinal);
        foreach (var fact in _store.Read(AgentKeys.Codex))
        {
            var session = fact.Source.SessionId;
            if (session is null || !parentSessions.Contains(session)) continue;
            if (fact.Source.SourcePath is { } path && replacing.Contains(path)) continue;
            if (fact.Source.CumulativeInputTokens is not { } input
                || fact.Source.CumulativeOutputTokens is not { } output) continue;
            AddCheckpoint(history, session, new CodexUsageCheckpoint(fact.Timestamp, input, output));
        }

        foreach (var turn in incoming)
        {
            var session = turn.Fact.Source.SessionId;
            if (session is null || !parentSessions.Contains(session) || turn.ForkedFromId is not null) continue;
            AddCheckpoint(history, session,
                new CodexUsageCheckpoint(turn.Fact.Timestamp, turn.CumulativeInput, turn.CumulativeOutput));
        }

        return history.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CodexUsageCheckpoint>)pair.Value,
            StringComparer.Ordinal);
    }

    private static void AddCheckpoint(
        IDictionary<string, List<CodexUsageCheckpoint>> history,
        string session,
        CodexUsageCheckpoint checkpoint)
    {
        if (!history.TryGetValue(session, out var checkpoints))
        {
            checkpoints = new List<CodexUsageCheckpoint>();
            history[session] = checkpoints;
        }
        checkpoints.Add(checkpoint);
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
