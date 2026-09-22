using System.Text.Json;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;

namespace AgentMonitoring.Consumption;

/// In-memory consumption facts plus optional JSON persistence. Append and
/// cursor updates happen under one lock so a crash mid-write cannot double-count.
public sealed class ConsumptionStore : IConsumptionStore, IConsumptionQuery
{
    private readonly string? _persistPath;
    private readonly object _gate = new();
    private readonly Dictionary<AgentKey, List<ConsumptionFact>> _facts = new();
    private readonly Dictionary<string, CodexFileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recordIds = new(StringComparer.Ordinal);

    public ConsumptionStore(string? persistPath = null)
    {
        _persistPath = persistPath;
        if (_persistPath is not null) Load();
    }

    public IReadOnlyList<ConsumptionFact> Read(AgentKey agent, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        lock (_gate)
        {
            if (!_facts.TryGetValue(agent, out var list)) return Array.Empty<ConsumptionFact>();
            IEnumerable<ConsumptionFact> query = list;
            if (from is { } start) query = query.Where(fact => fact.Timestamp >= start);
            if (to is { } end) query = query.Where(fact => fact.Timestamp < end);
            return query.ToList();
        }
    }

    IReadOnlyList<ConsumptionFact> IConsumptionStore.Read(AgentKey agent) => Read(agent);

    public ScanCursor? GetCursor(string sourceIdentity)
    {
        lock (_gate) return _files.TryGetValue(sourceIdentity, out var state) ? state.Cursor : null;
    }

    public CodexFileState? GetFileState(string sourceIdentity)
    {
        lock (_gate) return _files.TryGetValue(sourceIdentity, out var state) ? state : null;
    }

    public bool ContainsRecord(string recordId)
    {
        lock (_gate) return _recordIds.Contains(recordId);
    }

    public IReadOnlyList<CodexFileState> ReadFileStates()
    {
        lock (_gate) return _files.Values.ToList();
    }

    public void Commit(
        AgentKey agent,
        IReadOnlyList<ConsumptionFact> newFacts,
        IReadOnlyDictionary<string, CodexFileState> fileStates,
        IReadOnlyCollection<string>? replaceSourcePaths = null)
    {
        lock (_gate)
        {
            if (!_facts.TryGetValue(agent, out var list))
            {
                list = new List<ConsumptionFact>();
                _facts[agent] = list;
            }

            if (replaceSourcePaths is { Count: > 0 })
            {
                var replace = new HashSet<string>(replaceSourcePaths, StringComparer.OrdinalIgnoreCase);
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (!replace.Contains(list[i].Source.SourcePath ?? "")) continue;
                    _recordIds.Remove(list[i].Source.RecordId);
                    list.RemoveAt(i);
                }

                foreach (var path in replace)
                    _files.Remove(path);
            }

            foreach (var fact in newFacts)
            {
                if (!_recordIds.Add(fact.Source.RecordId)) continue;
                list.Add(fact);
            }

            foreach (var (path, state) in fileStates)
                _files[path] = state;

            SaveUnlocked();
        }
    }

    public void DropSource(AgentKey agent, string sourcePath)
    {
        lock (_gate)
        {
            if (_facts.TryGetValue(agent, out var list))
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(list[i].Source.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    _recordIds.Remove(list[i].Source.RecordId);
                    list.RemoveAt(i);
                }
            }

            _files.Remove(sourcePath);
            SaveUnlocked();
        }
    }

    private void Load()
    {
        if (_persistPath is null || !File.Exists(_persistPath)) return;
        try
        {
            var json = File.ReadAllText(_persistPath);
            var snapshot = JsonSerializer.Deserialize<PersistDto>(json);
            if (snapshot is null) return;
            foreach (var row in snapshot.Facts)
            {
                var fact = row.ToFact();
                if (!_recordIds.Add(fact.Source.RecordId)) continue;
                if (!_facts.TryGetValue(fact.Source.Agent, out var list))
                {
                    list = new List<ConsumptionFact>();
                    _facts[fact.Source.Agent] = list;
                }

                list.Add(fact);
            }

            foreach (var file in snapshot.Files)
                _files[file.Path] = file.ToState();
        }
        catch
        {
        }
    }

    private void SaveUnlocked()
    {
        if (_persistPath is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var snapshot = new PersistDto(
                CodexRolloutDiscoveryVersion: AgentIsland.Providers.Cost.Codex.CodexRolloutDiscovery.ParserVersion,
                Facts: _facts.Values.SelectMany(list => list).Select(FactDto.From).ToList(),
                Files: _files.Select(pair => FileDto.From(pair.Key, pair.Value)).ToList());
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch
        {
        }
    }

    private sealed record PersistDto(string CodexRolloutDiscoveryVersion, List<FactDto> Facts, List<FileDto> Files);

    private sealed record FactDto(
        string Agent,
        string RecordId,
        string? SessionId,
        string? ProjectId,
        string? AccountId,
        string? SourcePath,
        long? ByteOffset,
        DateTimeOffset Timestamp,
        string RawModel,
        string CanonicalModel,
        bool IsFallback,
        long Input,
        long Output,
        long CacheCreation,
        long CacheRead,
        long Reasoning,
        ReasoningAccounting ReasoningAccounting,
        ServiceTier ServiceTier,
        bool LongContext,
        double? OfficialCostUsd)
    {
        public static FactDto From(ConsumptionFact fact) => new(
            fact.Source.Agent.Value,
            fact.Source.RecordId,
            fact.Source.SessionId,
            fact.Source.ProjectId,
            fact.Source.AccountId,
            fact.Source.SourcePath,
            fact.Source.ByteOffset,
            fact.Timestamp,
            fact.Model.Raw,
            fact.Model.Canonical,
            fact.Model.IsFallback,
            fact.Tokens.Input,
            fact.Tokens.Output,
            fact.Tokens.CacheCreation,
            fact.Tokens.CacheRead,
            fact.Tokens.Reasoning,
            fact.Tokens.ReasoningAccounting,
            fact.Pricing.ServiceTier,
            fact.Pricing.LongContext,
            fact.Pricing.OfficialCostUsd);

        public ConsumptionFact ToFact() => new(
            new SourceRef(new AgentKey(Agent), RecordId, SessionId, ProjectId, AccountId, SourcePath, ByteOffset),
            Timestamp,
            new ModelRef(RawModel, CanonicalModel, IsFallback),
            new TokenBuckets(Input, Output, CacheCreation, CacheRead, Reasoning, ReasoningAccounting),
            new PricingContext(ServiceTier, LongContext, Timestamp, OfficialCostUsd));
    }

    private sealed record FileDto(
        string Path,
        long Position,
        string ParserVersion,
        long Size,
        long MtimeUtcTicks,
        string? SessionId,
        string? ForkedFromId,
        DateTimeOffset? SessionStartedAt,
        string? ProjectId,
        string CurrentModel,
        bool ModelIsFallback,
        ServiceTier ServiceTier,
        long? LastTotalInput,
        long? LastTotalOutput)
    {
        public static FileDto From(string path, CodexFileState state) => new(
            path,
            state.Cursor.Position,
            state.Cursor.ParserVersion,
            state.Cursor.Size,
            state.Cursor.MtimeUtcTicks,
            state.SessionId,
            state.ForkedFromId,
            state.SessionStartedAt,
            state.ProjectId,
            state.CurrentModel,
            state.ModelIsFallback,
            state.ServiceTier,
            state.LastTotalInput,
            state.LastTotalOutput);

        public CodexFileState ToState() => new(
            new ScanCursor(AgentKeys.Codex, Path, Position, ParserVersion, Size, MtimeUtcTicks, null),
            SessionId,
            ForkedFromId,
            SessionStartedAt,
            ProjectId,
            CurrentModel,
            ModelIsFallback,
            ServiceTier,
            LastTotalInput,
            LastTotalOutput);
    }
}
