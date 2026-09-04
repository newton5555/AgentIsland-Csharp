using System.Globalization;
using AgentIsland.Providers.Usage.DeepSeek;

namespace AgentIsland.UI;

/// Presentation helpers for DeepSeek's account balance. Currency snapshots
/// are not percentages, so this formatter never routes them through
/// QuotaDisplayModeStore or the existing WindowUsage labels.
internal static class DeepSeekBalanceText
{
    public static string Total(DeepSeekBalanceSnapshot snapshot)
    {
        if (snapshot.Balances.Count == 0) return "—";
        return string.Join(
            " · ",
            snapshot.Balances.Select(info =>
                snapshot.Balances.Count == 1
                    ? Amount(info, info.TotalBalance)
                    : info.Currency + " " + Amount(info, info.TotalBalance)));
    }

    public static string Details(DeepSeekBalanceSnapshot snapshot)
    {
        if (snapshot.Balances.Count == 0) return L10n.Tr("No balance details");
        return string.Join(
            " · ",
            snapshot.Balances.Select(info =>
                L10n.TrFormat(
                    "granted {0} · topped up {1}",
                    Amount(info, info.GrantedBalance),
                    Amount(info, info.ToppedUpBalance))));
    }

    public static string Availability(DeepSeekBalanceSnapshot snapshot) =>
        snapshot.IsAvailable ? L10n.Tr("available") : L10n.Tr("unavailable");

    public static string Amount(DeepSeekBalanceInfo info, decimal amount)
    {
        var number = Math.Abs(amount).ToString("N2", CultureInfo.InvariantCulture);
        var symbol = info.Currency.ToUpperInvariant() switch
        {
            "CNY" or "RMB" => "¥",
            "USD" => "$",
            "EUR" => "€",
            "JPY" => "¥",
            "GBP" => "£",
            _ => info.Currency.ToUpperInvariant() + " ",
        };
        return amount < 0 ? "-" + symbol + number : symbol + number;
    }
}
