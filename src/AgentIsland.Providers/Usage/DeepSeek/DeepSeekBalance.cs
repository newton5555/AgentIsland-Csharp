using System.Globalization;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Providers.Usage.DeepSeek;

/// One currency bucket returned by DeepSeek's account-balance endpoint.
/// Amounts are decimal strings in the API payload, so the provider model keeps
/// them as decimals instead of routing them through the percentage-window
/// usage model or binary floating point.
public sealed record DeepSeekBalanceInfo(
    string Currency,
    decimal TotalBalance,
    decimal GrantedBalance,
    decimal ToppedUpBalance);

/// The account-level balance snapshot. This is deliberately separate from
/// AppUsage: DeepSeek reports money/credit availability, not a 5h or weekly
/// percentage window.
public sealed record DeepSeekBalanceSnapshot(
    bool IsAvailable,
    IReadOnlyList<DeepSeekBalanceInfo> Balances);

/// Tolerant decoder for `GET /user/balance`.
public static class DeepSeekBalanceParser
{
    public static DeepSeekBalanceSnapshot? Parse(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var available = Bool(root, "is_available") ?? Bool(root, "isAvailable");
            if (available is not { } isAvailable) return null;

            var balances = new List<DeepSeekBalanceInfo>();
            var rows = Array(root, "balance_infos") ?? Array(root, "balanceInfos");
            if (rows is { } array)
            {
                foreach (var row in array.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    var currency = String(row, "currency");
                    if (string.IsNullOrWhiteSpace(currency)) continue;

                    // The official response contains all three amounts. Keep
                    // an individual row when a future server omits a zero
                    // bucket, treating the absent optional amount as zero.
                    var total = Decimal(row, "total_balance")
                        ?? Decimal(row, "totalBalance");
                    if (total is not { } totalBalance) continue;
                    var granted = Decimal(row, "granted_balance")
                        ?? Decimal(row, "grantedBalance")
                        ?? 0m;
                    var toppedUp = Decimal(row, "topped_up_balance")
                        ?? Decimal(row, "toppedUpBalance")
                        ?? 0m;
                    balances.Add(new DeepSeekBalanceInfo(
                        currency.Trim().ToUpperInvariant(),
                        totalBalance,
                        granted,
                        toppedUp));
                }
            }

            return new DeepSeekBalanceSnapshot(isAvailable, balances);
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? Array(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    private static string? String(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            }
            : null;

    private static decimal? Decimal(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when System.Decimal.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                => parsed,
            _ => null,
        };
    }
}
