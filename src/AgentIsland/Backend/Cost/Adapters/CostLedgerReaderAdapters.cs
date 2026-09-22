using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentMonitoring.Consumption;
using AgentMonitoring.Pricing;

namespace AgentIsland.Backend.Cost.Adapters;

public sealed class ClaudeCostLedgerReader : ICostLedgerReader
{
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(ClaudeLogReader.Scan(lookbackDays, ct));
    }
}

public sealed class CodexCostLedgerReader : ICostLedgerReader
{
    private readonly IConsumptionQuery? _query;
    private readonly IPricer _pricer;

    public CodexCostLedgerReader(IConsumptionQuery? query = null, IPricer? pricer = null)
    {
        _query = query;
        _pricer = pricer ?? new SnapshotPricer();
    }

    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
    {
        if (_query is null)
        {
            return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(CodexLogReader.Scan(lookbackDays, ct));
        }

        var from = DateTimeOffset.Now.AddDays(-Math.Max(0, lookbackDays));
        var facts = _query.Read(AgentKeys.Codex, from, null);
        var events = facts.Select(fact => FactMapping.ToTokenEvent(fact, _pricer)).ToList();
        return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(events);
    }
}

public sealed class GrokCostLedgerReader : ICostLedgerReader
{
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(GrokLogReader.Scan(lookbackDays, ct));
    }
}

public sealed class CursorCostLedgerReader : ICostLedgerReader
{
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(CursorLogReader.Scan(lookbackDays, ct));
    }
}

public sealed class DeepSeekCostLedgerReader : ICostLedgerReader
{
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(DeepSeekLogReader.Scan(lookbackDays, ct));
    }
}

public sealed class AntigravityCostLedgerReader : ICostLedgerReader
{
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(AntigravityLogReader.Scan(lookbackDays, ct));
    }
}
