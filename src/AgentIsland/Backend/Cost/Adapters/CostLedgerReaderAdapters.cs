using AgentIsland.Core;
using AgentIsland.Core.Agents;

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
    public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(CodexLogReader.Scan(lookbackDays, ct));
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
