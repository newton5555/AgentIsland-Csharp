using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Timestamp parser for the Grok endpoints' 6-digit fractional seconds
/// ("2026-07-31T15:48:23.488813+00:00"). .NET's ISO8601 parse already accepts
/// up to 7 fractional digits, so those land on the first attempt; the
/// trimming fallbacks cover longer fractions from other writer versions
/// instead of dropping the timestamp — a lost period end silently erases the
/// reset countdown.
public static class GrokTimestamp
{
    private static readonly Regex FractionPattern = new(@"\.\d+", RegexOptions.CultureInvariant);

    public static DateTimeOffset? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Jsonl.ParseIso8601(raw) is { } parsed) return parsed;

        var match = FractionPattern.Match(raw);
        if (!match.Success) return null;
        var head = raw[..match.Index];
        var tail = raw[(match.Index + match.Length)..];
        var trimmed = match.Value[..Math.Min(4, match.Value.Length)];
        return Jsonl.ParseIso8601(head + trimmed + tail) ?? Jsonl.ParseIso8601(head + tail);
    }
}

/// One per-product consumption row inside the weekly credit pool
/// (`config.productUsage`) — what the hover detail breaks the pool into.
/// UsedPercent is normalized to [0, 1] like the pool percent, and is null
/// when the endpoint lists a product without a percent: absence is data,
/// not zero.
public sealed record GrokProductUsage(string Product, double? UsedPercent);

/// What the island renders for Grok: the weekly credit pool (the number
/// SuperGrok users budget around) plus the monthly dollar budget. Serialized
/// through Preferences so the last good values survive a relaunch.
/// ProductUsage is optional so pre-1.8.1 cached snapshots keep decoding.
public sealed record GrokBillingSnapshot(
    double WeeklyUsedPercent,
    DateTimeOffset? WeeklyPeriodEnd = null,
    int? MonthlyUsedCents = null,
    int? MonthlyLimitCents = null,
    DateTimeOffset? MonthlyPeriodEnd = null,
    IReadOnlyList<GrokProductUsage>? ProductUsage = null);

/// Decoders for the two `cli-chat-proxy.grok.com/v1/billing` payloads. Kept
/// tolerant (like UsageFetcher) because the weekly response omits
/// `creditUsagePercent` entirely at zero usage — absence is data, not an
/// error, and treating it as a parse failure would blank the bar for anyone
/// who hasn't spent a credit yet this period.
public static class GrokBillingParser
{
    public sealed record WeeklyPool(
        double UsedPercent,
        DateTimeOffset? PeriodEnd,
        IReadOnlyList<GrokProductUsage> Products);

    public sealed record MonthlyBudget(
        int? UsedCents,
        int? LimitCents,
        DateTimeOffset? PeriodEnd);

    /// `GET /v1/billing?format=credits` — config.currentPeriod carries the
    /// weekly window; creditUsagePercent is 0–100.
    public static WeeklyPool? ParseWeekly(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (Jsonl.GetObject(root, "config") is not { } config) return null;
            var percent = Number(config, "creditUsagePercent") ?? Number(root, "creditUsagePercent") ?? 0;
            var end = (Jsonl.GetObject(config, "currentPeriod") is { } period ? Timestamp(period, "end") : null)
                ?? Timestamp(config, "billingPeriodEnd");
            return new WeeklyPool(
                Math.Min(1, Math.Max(0, percent / 100)),
                end,
                ProductRows(config));
        }
        catch
        {
            return null;
        }
    }

    /// `GET /v1/billing` — monthlyLimit/used are `{ "val": <cents> }`.
    public static MonthlyBudget? ParseMonthly(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (Jsonl.GetObject(doc.RootElement, "config") is not { } config) return null;
            return new MonthlyBudget(
                Cents(config, "used"),
                Cents(config, "monthlyLimit"),
                Timestamp(config, "billingPeriodEnd"));
        }
        catch
        {
            return null;
        }
    }

    /// "40" for whole dollars, "1.23" otherwise — the strip has no room for
    /// trailing ".00" noise. Caller prepends the currency symbol.
    public static string Dollars(int rawCents)
    {
        var cents = Math.Max(0, rawCents);
        return cents % 100 == 0
            ? (cents / 100).ToString(CultureInfo.InvariantCulture)
            : (cents / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// `config.productUsage` rows — proxy builds have shipped the percent
    /// under both `usagePercent` and `creditUsagePercent`, so accept either.
    private static IReadOnlyList<GrokProductUsage> ProductRows(JsonElement config)
    {
        if (!config.TryGetProperty("productUsage", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GrokProductUsage>();
        }
        var products = new List<GrokProductUsage>();
        foreach (var row in rows.EnumerateArray())
        {
            if (Jsonl.GetString(row, "product") is not { Length: > 0 } product) continue;
            var percent = Number(row, "usagePercent") ?? Number(row, "creditUsagePercent");
            products.Add(new GrokProductUsage(
                product,
                percent is { } value ? Math.Min(1, Math.Max(0, value / 100)) : null));
        }
        return products;
    }

    private static int? Cents(JsonElement parent, string property)
    {
        if (Jsonl.GetObject(parent, property) is not { } box) return null;
        if (Number(box, "val") is not { } value) return null;
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < int.MinValue || rounded > int.MaxValue) return null;
        return (int)rounded;
    }

    private static double? Number(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var number) ? number : (double?)null,
            JsonValueKind.String => double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (double?)null,
            _ => null,
        };
    }

    private static DateTimeOffset? Timestamp(JsonElement parent, string property) =>
        GrokTimestamp.Parse(Jsonl.GetString(parent, property));
}
