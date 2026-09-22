using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.BuiltIn;

namespace AgentIsland.Tests;

public class P0ConsumptionContractTests
{
    [Fact]
    public void ReasoningIncludedInOutput_DoesNotIncreaseWireOrBillable()
    {
        var included = new TokenBuckets(10, 20, 1, 2, 8, ReasoningAccounting.IncludedInOutput);
        Assert.Equal(33, included.WireTokens);
        Assert.Equal(30, included.BillableTokens);

        var separate = new TokenBuckets(10, 20, 1, 2, 8, ReasoningAccounting.Separate);
        Assert.Equal(41, separate.WireTokens);
        Assert.Equal(38, separate.BillableTokens);

        var absent = new TokenBuckets(10, 20, 1, 2, 0, ReasoningAccounting.Absent);
        Assert.Equal(33, absent.WireTokens);
        Assert.Equal(30, absent.BillableTokens);
    }

    [Fact]
    public void UnpricedQuote_HasNoAmount()
    {
        var source = new PriceSource("embedded-snapshot", "p0", new DateOnly(2026, 6, 10), DateTimeOffset.UnixEpoch);
        var quote = PricingQuote.Unpriced("unknown model", source);
        Assert.Equal(CostKind.Unpriced, quote.Kind);
        Assert.Null(quote.AmountUsd);
        Assert.False(quote.HasAmount);

        var estimated = PricingQuote.Estimated(0, source);
        Assert.Equal(0, estimated.AmountUsd);
        Assert.True(estimated.HasAmount);
    }

    [Fact]
    public void UnknownAccount_StaysNullOnFact()
    {
        var fact = SampleFact(accountId: null);
        Assert.Null(fact.Source.AccountId);
        Assert.Equal("codex", fact.Source.Agent.Value);
        Assert.False(fact.Model.IsFallback);
    }

    [Fact]
    public void ScanCursor_CarriesParserVersion()
    {
        var cursor = new ScanCursor(
            new AgentKey("codex"),
            @"sessions\rollout.jsonl",
            Position: 4096,
            ParserVersion: "codex-replay-v1",
            Size: 8192,
            MtimeUtcTicks: 1,
            ContentFingerprint: "abc");
        Assert.Equal("codex-replay-v1", cursor.ParserVersion);
        Assert.Equal(4096, cursor.Position);
    }

    [Fact]
    public void CurrentPricingTable_UnknownModelIsZero_ContractCallsThatUnpriced()
    {
        var unknown = new TokenEvent(
            TriggerTool.Codex,
            DateTimeOffset.UnixEpoch,
            "not-a-real-model",
            100, 10, 0, 0);
        Assert.False(Pricing.IsKnown(unknown.Model));
        Assert.Equal(0, unknown.Dollars);

        var source = new PriceSource("embedded-snapshot", "p0", Pricing.SnapshotDate, DateTimeOffset.UnixEpoch);
        var quote = PricingQuote.Unpriced("unknown model", source);
        Assert.Null(quote.AmountUsd);
    }

    [Fact]
    public void Catalog_AdvertisedCapabilities_AreTheP0Baseline()
    {
        var catalog = new BuiltInAgentCatalog();
        Assert.Equal(
            new[] { "claude", "codex", "antigravity", "grok", "cursor", "deepseek" },
            catalog.Modules.Select(module => module.Descriptor.Key.Value).ToArray());

        Assert.True(catalog.Find(new AgentKey("claude"))!.Descriptor.Supports(
            AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost |
            AgentCapabilities.SessionNavigation | AgentCapabilities.Reauthentication));
        Assert.True(catalog.Find(new AgentKey("codex"))!.Descriptor.Supports(
            AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost |
            AgentCapabilities.SessionNavigation | AgentCapabilities.Reauthentication));
        Assert.True(catalog.Find(new AgentKey("antigravity"))!.Descriptor.Supports(
            AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost |
            AgentCapabilities.SessionNavigation));
        Assert.True(catalog.Find(new AgentKey("grok"))!.Descriptor.Supports(
            AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost |
            AgentCapabilities.SessionNavigation));
        Assert.True(catalog.Find(new AgentKey("cursor"))!.Descriptor.Supports(
            AgentCapabilities.Activity | AgentCapabilities.Usage | AgentCapabilities.Cost |
            AgentCapabilities.SessionNavigation));
        Assert.Equal(
            AgentCapabilities.Activity | AgentCapabilities.Cost | AgentCapabilities.Balance,
            catalog.Find(new AgentKey("deepseek"))!.Descriptor.Capabilities);
    }

    private static ConsumptionFact SampleFact(string? accountId) => new(
        new SourceRef(
            new AgentKey("codex"),
            RecordId: "sess-replay-001:2026-07-16T10:00:01Z:100:10",
            SessionId: "sess-replay-001",
            ProjectId: null,
            AccountId: accountId,
            SourcePath: "01-same-file-replay.jsonl",
            ByteOffset: 0),
        DateTimeOffset.Parse("2026-07-16T10:00:01Z"),
        new ModelRef("gpt-5.4", "gpt-5.4", IsFallback: false),
        new TokenBuckets(100, 10, 0, 0, 0, ReasoningAccounting.IncludedInOutput),
        new PricingContext(ServiceTier.Unspecified, LongContext: false,
            DateTimeOffset.Parse("2026-07-16T10:00:01Z"), OfficialCostUsd: null));
}
