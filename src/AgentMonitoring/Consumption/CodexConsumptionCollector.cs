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
        var filesByPath = new Dictionary<string, CodexRolloutFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) filesByPath[file.FullPath] = file;

        var originalStates = new Dictionary<string, CodexFileState?>(StringComparer.OrdinalIgnoreCase);
        var fileStates = new Dictionary<string, CodexFileState>(StringComparer.OrdinalIgnoreCase);
        var turnsByPath = new Dictionary<string, List<CodexParsedTurn>>(StringComparer.OrdinalIgnoreCase);
        var replacing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullyRebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, file) in filesByPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }
            if (!info.Exists) continue;

            var size = info.Length;
            var mtime = info.LastWriteTimeUtc.Ticks;
            var existing = _store.GetFileState(path);
            originalStates[path] = existing;
            var rebuild = existing is null
                || existing.Cursor.ParserVersion != CodexRolloutDiscovery.ParserVersion
                || size < existing.Cursor.Position
                || (size == existing.Cursor.Position
                    && mtime != existing.Cursor.MtimeUtcTicks);

            if (!rebuild && size == existing!.Cursor.Position)
            {
                fileStates[path] = existing;
                continue;
            }

            if (TryParse(file, existing, rebuild, size, mtime) && rebuild)
                fullyRebuilt.Add(path);
        }

        if (Volatile.Read(ref _generation) != generation) return;

        var statesByPath = BuildStatesByPath();
        var parentHistory = ParentHistory(statesByPath.Values, _store.Read(AgentKeys.Codex), replacing);
        var baselineChanges = new List<string>();
        foreach (var (path, _) in filesByPath)
        {
            if (!originalStates.TryGetValue(path, out var original)
                || original is null
                || fullyRebuilt.Contains(path)
                || !fileStates.TryGetValue(path, out var current)
                || string.IsNullOrEmpty(current.ForkedFromId))
                continue;

            var currentBaseline = BaselineFor(current, parentHistory);
            if (!BaselineEquals(original, currentBaseline)) baselineChanges.Add(path);
        }

        // A newly available or corrected parent baseline changes the meaning of
        // every turn in its child transcript, so replay that source from byte 0.
        var unreconciled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in baselineChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = originalStates[path]!;
            if (!TryParse(filesByPath[path], original, rebuild: true, size: null, mtime: null))
            {
                // Keep the last committed cursor/facts and retry reconciliation
                // on a later collect instead of committing a partial rewrite.
                turnsByPath.Remove(path);
                replacing.Remove(path);
                fileStates[path] = original;
                unreconciled.Add(path);
                continue;
            }
            fullyRebuilt.Add(path);
        }

        if (Volatile.Read(ref _generation) != generation) return;

        statesByPath = BuildStatesByPath();
        parentHistory = ParentHistory(statesByPath.Values, _store.Read(AgentKeys.Codex), replacing);
        var baselinesByPath = new Dictionary<string, (long Input, long Output)?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, state) in statesByPath)
        {
            if (string.IsNullOrEmpty(state.ForkedFromId)) continue;
            baselinesByPath[path] = BaselineFor(state, parentHistory);
        }

        var incoming = turnsByPath.Values.SelectMany(turns => turns).ToList();
        var previouslyPassed = statesByPath.Values
            .Where(state => state.ForkReplayPassedBaseline)
            .Select(state => CodexReplayPlan.ChildIdentity(
                state.SessionId, state.Cursor.SourceIdentity))
            .ToHashSet(StringComparer.Ordinal);
        var replay = CodexReplayPlan.Apply(incoming, parentHistory, previouslyPassed);
        var facts = new List<ConsumptionFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in replay.KeptTurns)
        {
            if (!seen.Add(turn.Fact.Source.RecordId)) continue;
            var path = turn.Fact.Source.SourcePath;
            var replacingThis = path is not null && replacing.Contains(path);
            if (!replacingThis && _store.ContainsRecord(turn.Fact.Source.RecordId)) continue;
            facts.Add(turn.Fact);
        }

        foreach (var path in fileStates.Keys.ToList())
        {
            if (unreconciled.Contains(path)) continue;
            var state = fileStates[path];
            if (string.IsNullOrEmpty(state.ForkedFromId)) continue;
            var baseline = baselinesByPath.TryGetValue(path, out var value) ? value : null;
            var child = CodexReplayPlan.ChildIdentity(state.SessionId, path);
            fileStates[path] = state with
            {
                ForkBaselineInput = baseline?.Input,
                ForkBaselineOutput = baseline?.Output,
                ForkReplayPassedBaseline = replay.PassedBaselineChildren.Contains(child),
            };
        }

        if (Volatile.Read(ref _generation) != generation) return;
        _store.Commit(AgentKeys.Codex, facts, fileStates, replacing.ToList());

        bool TryParse(
            CodexRolloutFile file,
            CodexFileState? existing,
            bool rebuild,
            long? size,
            long? mtime)
        {
            var sourcePath = file.FullPath;
            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists) return false;
                size ??= info.Length;
                mtime ??= info.LastWriteTimeUtc.Ticks;

                var start = rebuild || existing is null ? 0L : existing.Cursor.Position;
                var parseState = !rebuild && existing is not null
                    ? FromFileState(existing)
                    : new CodexParseState();
                var (turns, nextState, nextOffset, ok) = CodexTranscriptParser.ParseFromOffset(
                    sourcePath, parseState, start);
                if (!ok) return false;

                var history = rebuild
                    ? new List<CodexUsageCheckpoint>()
                    : existing?.UsageHistory?.ToList() ?? new List<CodexUsageCheckpoint>();
                history.AddRange(turns.Select(turn => new CodexUsageCheckpoint(
                    turn.Fact.Timestamp, turn.CumulativeInput, turn.CumulativeOutput)));

                if (rebuild) replacing.Add(sourcePath);
                turnsByPath[sourcePath] = turns;
                fileStates[sourcePath] = ToFileState(
                    sourcePath,
                    nextState,
                    nextOffset,
                    size.Value,
                    mtime.Value,
                    history,
                    rebuild ? null : existing?.ForkBaselineInput,
                    rebuild ? null : existing?.ForkBaselineOutput,
                    rebuild ? false : existing?.ForkReplayPassedBaseline ?? false);
                return true;
            }
            catch
            {
                if (existing is not null) fileStates[sourcePath] = existing;
                return false;
            }
        }

        Dictionary<string, CodexFileState> BuildStatesByPath()
        {
            var states = _store.ReadFileStates().ToDictionary(
                state => state.Cursor.SourceIdentity,
                state => state,
                StringComparer.OrdinalIgnoreCase);
            foreach (var (path, state) in fileStates) states[path] = state;
            return states;
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<CodexUsageCheckpoint>> ParentHistory(
        IEnumerable<CodexFileState> states,
        IReadOnlyList<ConsumptionFact> persistedFacts,
        IReadOnlySet<string> replacing)
    {
        var history = new Dictionary<string, List<CodexUsageCheckpoint>>(StringComparer.Ordinal);
        var sourcesWithHistory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            if (state.UsageHistory is null) continue;
            sourcesWithHistory.Add(state.Cursor.SourceIdentity);
            var session = state.SessionId;
            if (string.IsNullOrEmpty(session)) continue;
            foreach (var checkpoint in state.UsageHistory)
                AddCheckpoint(history, session, checkpoint);
        }

        // Older persisted states have no raw checkpoint timeline. Their billed
        // facts still provide a best-effort history until the source is reparsed.
        foreach (var fact in persistedFacts)
        {
            var session = fact.Source.SessionId;
            if (session is null) continue;
            if (fact.Source.SourcePath is { } path
                && (replacing.Contains(path) || sourcesWithHistory.Contains(path))) continue;
            if (fact.Source.CumulativeInputTokens is not { } input
                || fact.Source.CumulativeOutputTokens is not { } output) continue;
            AddCheckpoint(history, session, new CodexUsageCheckpoint(fact.Timestamp, input, output));
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

    private static (long Input, long Output)? BaselineFor(
        CodexFileState state,
        IReadOnlyDictionary<string, IReadOnlyList<CodexUsageCheckpoint>> history)
    {
        var firstTurn = state.FirstTurnTimestamp
            ?? state.UsageHistory?.Select(item => (DateTimeOffset?)item.Timestamp).Min();
        return CodexReplayPlan.BaselineFor(
            state.ForkedFromId,
            state.SessionStartedAt,
            firstTurn,
            history);
    }

    private static bool BaselineEquals(
        CodexFileState state,
        (long Input, long Output)? baseline) =>
        baseline is { } value
            ? state.ForkBaselineInput == value.Input && state.ForkBaselineOutput == value.Output
            : state.ForkBaselineInput is null && state.ForkBaselineOutput is null;

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
        FirstTurnTimestamp = state.FirstTurnTimestamp,
    };

    private static CodexFileState ToFileState(
        string path,
        CodexParseState state,
        long position,
        long size,
        long mtime,
        IReadOnlyList<CodexUsageCheckpoint> usageHistory,
        long? forkBaselineInput,
        long? forkBaselineOutput,
        bool forkReplayPassedBaseline) =>
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
            state.LastTotalOutput,
            state.FirstTurnTimestamp,
            usageHistory,
            forkBaselineInput,
            forkBaselineOutput,
            forkReplayPassedBaseline);
}
