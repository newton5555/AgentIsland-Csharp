using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// One billing-period allowance, the number Cursor's own usage bar shows.
/// Cursor meters a single included-usage pool per billing cycle — not a
/// 5h/weekly pair — so the strip shows one meter and one reset date.
///   UsedPercent — 0…1 of the included allowance consumed.
///   PeriodEnd   — end of the current billing cycle; null when the payload
///                 omits it, which leaves the ~30-day cycle length as the
///                 only thing the label can say.
///   PlanName    — "free" / "pro" / … from GetPlanInfo, lowercased.
public sealed record CursorUsageSnapshot(
    double UsedPercent,
    DateTimeOffset? PeriodEnd,
    string? PlanName = null);

/// Cursor's real numbers, from the RPC its own usage bar reads:
///   POST api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage
///   POST …/GetPlanInfo
/// The legacy cursor.com/api/usage still answers but reports a retired
/// per-request counter — zeros and a null cap on current plans — so it is
/// deliberately not used. Verified live 2026-08-08 (free tier): 200 with
/// planUsage.totalPercentUsed and a real billingCycleEnd.
public static class CursorUsageFetcher
{
    public abstract record Outcome
    {
        public sealed record Success(CursorUsageSnapshot Snapshot) : Outcome;
        /// 401/403 from the dashboard — the Cursor login itself is stale.
        public sealed record ReauthRequired : Outcome;
        public sealed record Failed(string Message) : Outcome;
        /// No state db — Cursor never signed in on this machine.
        public sealed record NotInstalled : Outcome;
    }

    private const string RpcBase = "https://api2.cursor.sh/aiserver.v1.DashboardService/";

    /// Cursor gates these endpoints on a Connect client User-Agent the same
    /// way Anthropic gates its usage endpoint on the CLI's.
    private const string UserAgent = "connect-es/1.6.1";

    /// Never let a wedged tunnel outlive the poll that started it. The shared
    /// HttpClient's own timeout is longer, so this one has to be per-request.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    public static async Task<Outcome> Fetch(CancellationToken ct = default)
    {
        if (!CursorCredentials.Exists()) return new Outcome.NotInstalled();
        if (CursorCredentials.ReadSession() is not { } session) return new Outcome.ReauthRequired();

        switch (await Post("GetCurrentPeriodUsage", session, ct))
        {
            case HttpOutcome.Transport transport:
                return new Outcome.Failed(transport.Message);
            case HttpOutcome.Response { Status: 401 or 403 }:
                return new Outcome.ReauthRequired();
            case HttpOutcome.Response response:
            {
                if (response.Status != 200) return new Outcome.Failed($"http {response.Status}");
                if (ParseUsage(response.Body) is not { } snapshot) return new Outcome.Failed("parse error");
                // Plan is garnish — a failure only costs the chip, never the
                // bar, and the editor's own local copy backs it up.
                string? plan = null;
                if (await Post("GetPlanInfo", session, ct) is HttpOutcome.Response { Status: 200 } planned)
                {
                    plan = ParsePlanName(planned.Body);
                }
                return new Outcome.Success(
                    snapshot with { PlanName = plan ?? CursorCredentials.CachedPlan() });
            }
            default:
                return new Outcome.Failed("bad response");
        }
    }

    // MARK: - Parsing (unit-tested)

    public static CursorUsageSnapshot? ParseUsage(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (Jsonl.GetObject(root, "planUsage") is not { } usage) return null;
            // totalPercentUsed covers the whole included allowance (agent +
            // API); when absent, the larger half is the better "how close am
            // I" signal since only one is usually in play.
            var percent = NumberValue(usage, "totalPercentUsed")
                ?? Math.Max(
                    NumberValue(usage, "autoPercentUsed") ?? 0,
                    NumberValue(usage, "apiPercentUsed") ?? 0);
            return new CursorUsageSnapshot(
                Math.Min(1, Math.Max(0, percent / 100)),
                EpochMillisDate(NumberValue(root, "billingCycleEnd")));
        }
        catch
        {
            return null;
        }
    }

    public static string? ParsePlanName(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (Jsonl.GetObject(doc.RootElement, "planInfo") is not { } info) return null;
            var name = Jsonl.GetString(info, "planName");
            return string.IsNullOrWhiteSpace(name) ? null : name.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// Connect encodes int64 as a JSON STRING ("1784404059475"), so every
    /// numeric read here has to accept both that and a plain number.
    public static double? NumberValue(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
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

    private static DateTimeOffset? EpochMillisDate(double? millis)
    {
        // Outside the representable range the payload is junk, not a date.
        if (millis is not { } value || value <= 0 || value > 253402300799999d) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)value);
    }

    // MARK: - HTTP

    private abstract record HttpOutcome
    {
        public sealed record Response(int Status, byte[] Body) : HttpOutcome;
        public sealed record Transport(string Message) : HttpOutcome;
    }

    private static async Task<HttpOutcome> Post(
        string method, CursorCredentials.Session session, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RpcBase + method);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.AccessToken);
            // Connect refuses a request that doesn't state its protocol version.
            request.Headers.TryAddWithoutValidation("connect-protocol-version", "1");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            // Bare "application/json" — the charset parameter StringContent
            // appends is not part of the Connect codec name.
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await Http.Client.SendAsync(request, timeout.Token);
            return new HttpOutcome.Response(
                (int)response.StatusCode,
                await response.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        catch (Exception error)
        {
            return new HttpOutcome.Transport(
                error is HttpRequestException or OperationCanceledException
                    ? L10n.Tr("network drop")
                    : error.Message);
        }
    }
}
