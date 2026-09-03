namespace AgentIsland.Core;

public static class Formatting
{
    /// Single-unit compact duration: "45s", "12m", "3h", "4d" — matches the
    /// macOS Duration.compact used in reset countdowns and captions.
    public static string CompactDuration(TimeSpan span)
    {
        var seconds = Math.Max(0, span.TotalSeconds);
        if (seconds < 60) return $"{(int)seconds}s";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m";
        if (seconds < 86400) return $"{(int)(seconds / 3600)}h";
        return $"{(int)(seconds / 86400)}d";
    }

    /// The one percent readout in the app: a 0-1 fraction as whole points.
    ///
    /// Away-from-zero, not .NET's default banker's rounding. Swift's
    /// `WindowUsage.percentInt` is `Int((usedPercent * 100).rounded())`, which
    /// rounds .5 up; `Math.Round` alone rounds it to even, so an exact 0.125
    /// printed 12% on Windows and 13% on macOS — and the tray, the panel and
    /// the report cards each have to agree with the other platform AND with
    /// each other.
    public static int PercentInt(double fraction) =>
        (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);

    /// "$146.61" / "$1,510.80" — invariant thousands separators.
    public static string Money(double dollars) =>
        "$" + dollars.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    /// "941", "12.4k", "211.2M", "2.17B".
    public static string CompactTokens(long tokens)
    {
        // Boundaries are 999_500, not 1_000_000: at 999_500 the "0"-rounding
        // below already renders 1000, so promote to the next unit there to
        // print "1.0M" instead of "1000k".
        return tokens switch
        {
            < 1_000 => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            < 999_500 => Trim(tokens / 1_000.0) + "k",
            < 999_500_000 => Trim(tokens / 1_000_000.0) + "M",
            _ => Trim(tokens / 1_000_000_000.0) + "B",
        };

        static string Trim(double value) =>
            value.ToString(value >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// Long-form "when it happens" countdown: "4小时后"/"3分钟后" in zh,
    /// "in 4h"/"in 3m" in en — the trigger-page reset caption format.
    public static string LongCountdown(TimeSpan until, bool chinese)
    {
        var seconds = Math.Max(0, until.TotalSeconds);
        if (chinese)
        {
            if (seconds < 60) return "1分钟内";
            if (seconds < 3600) return $"{(int)(seconds / 60)}分钟后";
            if (seconds < 86400) return $"{(int)(seconds / 3600)}小时后";
            return $"{(int)(seconds / 86400)}天后";
        }
        if (seconds < 60) return "under 1m";
        if (seconds < 3600) return $"in {(int)(seconds / 60)}m";
        if (seconds < 86400) return $"in {(int)(seconds / 3600)}h";
        return $"in {(int)(seconds / 86400)}d";
    }

    /// Relative "synced" label with second granularity under a minute:
    /// "58秒前", "2 分钟前", "1 小时前" — matching the macOS abbreviated
    /// relative formatter.
    public static string RelativeAgo(TimeSpan since, bool chinese)
    {
        var seconds = Math.Max(0, since.TotalSeconds);
        if (seconds < 5) return chinese ? "刚刚" : "just now";
        if (seconds < 60)
        {
            var s = (int)seconds;
            return chinese ? $"{s}秒前" : $"{s}s ago";
        }
        if (seconds < 3600)
        {
            var m = (int)(seconds / 60);
            return chinese ? $"{m} 分钟前" : $"{m}m ago";
        }
        var h = (int)(seconds / 3600);
        return chinese ? $"{h} 小时前" : $"{h}h ago";
    }
}
