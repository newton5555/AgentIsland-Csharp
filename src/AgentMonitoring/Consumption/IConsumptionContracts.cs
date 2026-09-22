using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;

namespace AgentMonitoring.Consumption;

public interface IConsumptionStore
{
    IReadOnlyList<ConsumptionFact> Read(AgentKey agent);
    ScanCursor? GetCursor(string sourceIdentity);
    CodexFileState? GetFileState(string sourceIdentity);
    bool ContainsRecord(string recordId);
    IReadOnlyList<CodexFileState> ReadFileStates();
    void Commit(
        AgentKey agent,
        IReadOnlyList<ConsumptionFact> newFacts,
        IReadOnlyDictionary<string, CodexFileState> fileStates,
        IReadOnlyCollection<string>? replaceSourcePaths = null);
    void DropSource(AgentKey agent, string sourcePath);
}

public interface IConsumptionQuery
{
    IReadOnlyList<ConsumptionFact> Read(AgentKey agent, DateTimeOffset? from = null, DateTimeOffset? to = null);
}

public interface IConsumptionCollector
{
    Task CollectAsync(AgentKey agent, CancellationToken cancellationToken = default);
}

public sealed record CodexFileState(
    ScanCursor Cursor,
    string? SessionId,
    string? ForkedFromId,
    DateTimeOffset? SessionStartedAt,
    string? ProjectId,
    string CurrentModel,
    bool ModelIsFallback,
    ServiceTier ServiceTier,
    long? LastTotalInput,
    long? LastTotalOutput);
