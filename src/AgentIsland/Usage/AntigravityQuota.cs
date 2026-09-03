using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentIsland.Usage;

/// One quota bucket from the local language server's
/// `RetrieveUserQuotaSummary`, normalized to the app's used-percent
/// vocabulary (the endpoint reports how much is *left*).
///
/// Antigravity pools by model family rather than by model: a real account
/// returns exactly two buckets — `gemini-weekly` covering Gemini Flash and
/// Pro, and `3p-weekly` covering Claude and GPT. Paid tiers are documented
/// to add 5h windows, so Window is kept and the count is never assumed.
public sealed record AntigravityQuotaBucket(
    string BucketId,
    string GroupLabel,
    string? Window,
    double UsedPercent,
    DateTimeOffset? ResetAt = null)
{
    public const double WeekSeconds = 7 * 24 * 60 * 60;

    /// How long this bucket's window runs, for the island pill's elapsed
    /// arc. Every bucket a real account returns is weekly; paid tiers are
    /// documented to add a 5h window, so that is handled rather than
    /// assumed away, and anything unrecognized falls back to weekly.
    [JsonIgnore]
    public double PeriodSeconds => (Window ?? "").ToLowerInvariant() switch
    {
        "5h" or "five_hour" or "fivehour" => 5 * 60 * 60,
        "daily" or "1d" => 24 * 60 * 60,
        _ => WeekSeconds,
    };

    /// Compact name for the island strip. Known pools get a short hand-set
    /// label; anything new falls back to Google's group name so a bucket we
    /// have never seen still reads as itself instead of a raw id.
    [JsonIgnore]
    public string ShortLabel
    {
        get
        {
            var family = BucketId.Split('-').FirstOrDefault() ?? BucketId;
            switch (family.ToLowerInvariant())
            {
                case "gemini": return "Gemini";
                case "3p": return "Claude·GPT";
                default:
                    var trimmed = GroupLabel
                        .Replace(" models", "", StringComparison.OrdinalIgnoreCase)
                        .Trim();
                    return trimmed.Length == 0 ? family : trimmed;
            }
        }
    }
}

/// What the island renders for Antigravity. Serializable so the last good
/// values survive a relaunch — and, more importantly, survive Antigravity
/// not running, which is the only time its quota is unreadable at all.
public sealed class AntigravityQuotaSnapshot
{
    public IReadOnlyList<AntigravityQuotaBucket> Buckets { get; init; } =
        Array.Empty<AntigravityQuotaBucket>();

    /// Raw tier id from `GetUserStatus.userTier` ("free-tier", …).
    public string? TierId { get; init; }

    /// Google's own tier name, shown verbatim.
    public string? TierLabel { get; init; }

    /// Google's explanation of how the shared pools burn down. Displayed as
    /// given rather than paraphrased — the rules are theirs, not ours.
    public string? Note { get; init; }

    public AntigravityQuotaSnapshot()
    {
    }

    public AntigravityQuotaSnapshot(
        IReadOnlyList<AntigravityQuotaBucket> buckets,
        string? tierId = null,
        string? tierLabel = null,
        string? note = null)
    {
        Buckets = buckets;
        TierId = tierId;
        TierLabel = tierLabel;
        Note = note;
    }

    /// Gemini pool buckets: Antigravity provides both a 5-hour rolling pool
    /// ("gemini-5h") and a weekly pool ("gemini-weekly") for Gemini models.
    [JsonIgnore]
    public AntigravityQuotaBucket? FiveHour =>
        Buckets.FirstOrDefault(b => b.BucketId.Contains("5h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(b.Window, "5h", StringComparison.OrdinalIgnoreCase))
        ?? (PrimaryBucket is { PeriodSeconds: <= 86400 } p ? p : null);

    [JsonIgnore]
    public AntigravityQuotaBucket? Weekly =>
        Buckets.FirstOrDefault(b => b.BucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(b.Window, "weekly", StringComparison.OrdinalIgnoreCase))
        ?? (PrimaryBucket is { PeriodSeconds: > 86400 } p ? p : null);

    [JsonIgnore]
    private AntigravityQuotaBucket? PrimaryBucket =>
        Buckets.FirstOrDefault(b => b.BucketId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        ?? Buckets.FirstOrDefault(b => b.GroupLabel.Contains("gemini", StringComparison.OrdinalIgnoreCase))
        ?? Buckets.FirstOrDefault();

    /// The primary pool this app surfaces for the island pill and single-window fallbacks.
    [JsonIgnore]
    public AntigravityQuotaBucket? Primary => FiveHour ?? PrimaryBucket;
}

/// Decoders for the local language server's JSON replies. Absence is data
/// here too — an account can legitimately report zero buckets.
public static class AntigravityQuotaParser
{
    public sealed record UserProfile(string? Email, string? TierId, string? TierLabel);

    /// `RetrieveUserQuotaSummary` → `{response:{groups:[{displayName,
    /// buckets:[{bucketId, window, remainingFraction, resetTime}]}],
    /// description}}`. The envelope key is not stable across versions
    /// (`response`, `summary`, or the groups sitting at the root), so all
    /// three are accepted.
    public static (IReadOnlyList<AntigravityQuotaBucket> Buckets, string? Note)? ParseQuotaSummary(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var envelope = Object(root, "response") ?? Object(root, "summary") ?? root;
            if (!envelope.TryGetProperty("groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return (Array.Empty<AntigravityQuotaBucket>(), null);
            }

            var buckets = new List<AntigravityQuotaBucket>();
            foreach (var group in groups.EnumerateArray())
            {
                var groupLabel = NonEmpty(String(group, "displayName")) ?? "";
                if (!group.TryGetProperty("buckets", out var rows)
                    || rows.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in rows.EnumerateArray())
                {
                    // A disabled bucket is one the account cannot use at
                    // all; rendering it as "100% left" would invent headroom.
                    if (row.TryGetProperty("disabled", out var disabled)
                        && disabled.ValueKind == JsonValueKind.True) continue;
                    if (NonEmpty(String(row, "bucketId")) is not { } bucketId) continue;
                    // Missing remainingFraction drops the row rather than
                    // claiming 100% headroom.
                    if (Fraction(row, "remainingFraction") is not { } remaining) continue;
                    buckets.Add(new AntigravityQuotaBucket(
                        bucketId,
                        groupLabel,
                        NonEmpty(String(row, "window")),
                        Math.Min(1, Math.Max(0, 1 - remaining)),
                        Timestamp(row, "resetTime")));
                }
            }
            return (buckets, NonEmpty(String(envelope, "description")));
        }
        catch
        {
            return null;
        }
    }

    /// `GetUserStatus` → identity and tier. The tier comes from
    /// `userTier.name`, never `planStatus.planInfo.planName`: on a real free
    /// account those read "Antigravity Starter Quota" and "Pro" respectively,
    /// and planName is the wrong one (a field inherited from Windsurf).
    /// Shown verbatim — Google keeps minting tier names and mapping them to
    /// an enum would only go stale.
    public static UserProfile? ParseUserStatus(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var status = Object(root, "userStatus") ?? root;
            var tier = Object(status, "userTier");
            return new UserProfile(
                NonEmpty(String(status, "email")),
                tier is { } t1 ? NonEmpty(String(t1, "id")) : null,
                tier is { } t2 ? NonEmpty(String(t2, "name")) : null);
        }
        catch
        {
            return null;
        }
    }

    /// The tier chip has to read like its neighbours — Codex says PRO,
    /// Claude says MAX — and Google's tier names are sentences: "Antigravity
    /// Starter Quota", "Google AI Ultra". Dropping the filler words leaves
    /// the part that identifies the plan ("STARTER", "AI ULTRA").
    public static string? CompactTierBadge(string? label, string? tierId)
    {
        if (string.IsNullOrEmpty(label)) return null;
        var filler = new HashSet<string> { "antigravity", "google", "quota", "plan", "tier" };
        var words = label
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !filler.Contains(w.ToLowerInvariant()))
            .ToArray();
        if (words.Length == 0)
        {
            return tierId == "free-tier" ? "FREE" : label.ToUpperInvariant();
        }
        return string.Join(' ', words).ToUpperInvariant();
    }

    /// `remainingFraction` arrives as a plain 0...1 number, but the same
    /// field has been seen oneof-expanded into an object by other Connect
    /// clients, so both are accepted.
    private static double? Fraction(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (!row.TryGetProperty(property, out var value)) return null;
        if (Number(value) is { } direct) return direct;
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty("value", out var inner) && Number(inner) is { } wrapped) return wrapped;
        if (value.TryGetProperty("remainingFraction", out var nested)) return Number(nested);
        return null;
    }

    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDouble(out var n) ? n : null,
        JsonValueKind.String => double.TryParse(
            value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null,
        _ => null,
    };

    private static DateTimeOffset? Timestamp(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (!row.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            return GrokTimestamp.Parse(value.GetString());
        }
        if (Number(value) is { } seconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                (long)(seconds > 1e11 ? seconds : seconds * 1000));
        }
        return null;
    }

    private static JsonElement? Object(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? String(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NonEmpty(string? raw) => string.IsNullOrEmpty(raw) ? null : raw;
}
