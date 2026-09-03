using AgentIsland.Core;

namespace AgentIsland.Cost;

/// One billable model call reconstructed from a local log line.
public sealed record TokenEvent(
    TriggerTool Provider,
    DateTimeOffset Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    double? SelfReportedCostUSD = null)
{
    /// ccusage parity: everything that crossed the wire.
    public long WireTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// Matches Anthropic's claude.ai stats panel, which excludes cache.
    public long BillableTokens => InputTokens + OutputTokens;

    /// Dollar cost for this event. A provider that ships its own figure
    /// (Grok's costUsdTicks) sets <see cref="SelfReportedCostUSD"/>, and when
    /// present it OVERRIDES the Pricing-table computation — the provider's own
    /// number wins. Table-priced providers (Claude, Codex, Cursor) leave it
    /// null and fall back to the embedded rate snapshot.
    public double Dollars => SelfReportedCostUSD ?? Pricing.Cost(this);
}
