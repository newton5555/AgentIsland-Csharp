using System;
using System.Collections.Generic;

namespace AgentIsland.Core.Usage;

/// <summary>
/// One currency balance bucket returned by an account balance provider.
/// Uses decimal amounts for financial precision. Legitimate zero balances and
/// negative balances (arrears/debt) are fully supported and distinct from NoData.
/// </summary>
public sealed record AccountBalanceEntry(
    string Currency,
    decimal Total,
    decimal Granted = 0m,
    decimal ToppedUp = 0m)
{
    public bool IsInArrears => Total < 0m;
}

/// <summary>
/// Cross-platform account-level monetary balance snapshot.
/// Strictly separated from percentage-window rate limit models (AppUsage/WindowUsage)
/// and log-based token spend models (ProviderCostSummary).
/// </summary>
public sealed record AccountBalanceSnapshot(
    bool IsAvailable,
    IReadOnlyList<AccountBalanceEntry> Entries)
{
    public static readonly AccountBalanceSnapshot Empty = new(false, Array.Empty<AccountBalanceEntry>());

    public AccountBalanceSnapshot(
        bool isAvailable,
        string currency,
        decimal total,
        decimal granted = 0m,
        decimal toppedUp = 0m)
        : this(isAvailable, new[] { new AccountBalanceEntry(currency, total, granted, toppedUp) })
    {
    }

    public AccountBalanceEntry? PrimaryEntry => Entries.Count > 0 ? Entries[0] : null;

    public string? Currency => PrimaryEntry?.Currency;

    public decimal Total => PrimaryEntry?.Total ?? 0m;

    public decimal Granted => PrimaryEntry?.Granted ?? 0m;

    public decimal ToppedUp => PrimaryEntry?.ToppedUp ?? 0m;

    public bool HasEntries => Entries.Count > 0;
}
