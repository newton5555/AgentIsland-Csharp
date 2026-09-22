using AgentIsland.Core.Usage;

namespace AgentMonitoring.Quotas;

/// Shared merge rule: a failed window must not replace a good reading.
/// "no data" means the provider omitted that window, not a fetch failure.
public static class QuotaMerge
{
    public static bool IsFailedWindow(WindowUsage usage) =>
        usage.HasError && usage.UsedPercent == 0 && usage.Error != "no data";

    public static bool IsErrorOnly(AppUsage usage) =>
        usage.FiveHour.Error is not null && usage.Weekly.Error is not null
        && usage.FiveHour.UsedPercent == 0 && usage.Weekly.UsedPercent == 0;

    public static AppUsage Apply(AppUsage? existing, AppUsage fetched)
    {
        if (existing is null) return fetched;
        if (IsErrorOnly(fetched) && !IsErrorOnly(existing))
        {
            var error = fetched.FiveHour.Error ?? fetched.Weekly.Error;
            return new AppUsage(
                existing.FiveHour with { Error = error },
                existing.Weekly with { Error = error },
                existing.Plan,
                existing.ResetCards,
                existing.ResetCardDetails);
        }

        return new AppUsage(
            KeepWindow(existing.FiveHour, fetched.FiveHour),
            KeepWindow(existing.Weekly, fetched.Weekly),
            fetched.Plan,
            fetched.ResetCards,
            fetched.ResetCardDetails);
    }

    private static WindowUsage KeepWindow(WindowUsage previous, WindowUsage fetched)
    {
        if (!IsFailedWindow(fetched) || IsFailedWindow(previous))
            return fetched;
        return previous with { Error = fetched.Error };
    }
}
