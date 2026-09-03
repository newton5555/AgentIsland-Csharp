using System.Net.Http;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// Fetches the Grok weekly credit pool + monthly budget from the CLI's own
/// proxy (`cli-chat-proxy.grok.com/v1/billing`), authenticating with the
/// %USERPROFILE%\.grok\auth.json access token and refreshing it through
/// auth.x.ai when it is expired or rejected. Display-only — no transcript
/// monitoring, no alarms live here.
public static class GrokUsageFetcher
{
    public abstract record Outcome
    {
        public sealed record Success(GrokBillingSnapshot Snapshot) : Outcome;
        /// Refresh path exhausted — the user has to run `grok login`.
        public sealed record ReauthRequired : Outcome;
        public sealed record Failed(string Message) : Outcome;
        /// No auth.json — the Grok CLI never logged in on this machine.
        public sealed record NotInstalled : Outcome;
    }

    private const string BillingBase = "https://cli-chat-proxy.grok.com/v1/billing";
    private const string ClientVersion = "0.2.114";

    /// The CLI stamps its own platform into the agent string; a Windows build
    /// claiming macOS would be the odd request in xAI's logs, not the safe one.
    private const string UserAgent = "grok-pager/0.2.114 grok-shell/0.2.114 (windows; x86_64)";

    public static async Task<Outcome> Fetch(CancellationToken ct = default)
    {
        var authPath = GrokAuthFile.DefaultPath;
        if (GrokAuthFile.LoadEntry(authPath) is not { } entry) return new Outcome.NotInstalled();

        // Proactive refresh near expiry (the access token lives ~6h). A failed
        // refresh still tries the billing call with the old token — clock skew
        // can make a perfectly good token look expired.
        if (GrokAuthFile.NeedsRefresh(entry) && entry.RefreshToken is not null)
        {
            if (await Refresh(entry, authPath, ct) is { } proactive) entry = proactive;
        }

        switch (await FetchBilling(entry.AccessToken, ct))
        {
            case BillingResult.Success success:
                return new Outcome.Success(success.Snapshot);
            case BillingResult.Failed failed:
                return new Outcome.Failed(failed.Message);
            case BillingResult.Unauthorized:
            {
                GrokAuthEntry? refreshed = entry.RefreshToken is null
                    ? null
                    : await Refresh(entry, authPath, ct);
                if (refreshed is null) return new Outcome.ReauthRequired();
                return await FetchBilling(refreshed.AccessToken, ct) switch
                {
                    BillingResult.Success retried => new Outcome.Success(retried.Snapshot),
                    BillingResult.Failed retryFailed => new Outcome.Failed(retryFailed.Message),
                    _ => new Outcome.ReauthRequired(),
                };
            }
            default:
                return new Outcome.Failed("parse error");
        }
    }

    // MARK: - Billing

    private abstract record BillingResult
    {
        public sealed record Success(GrokBillingSnapshot Snapshot) : BillingResult;
        public sealed record Unauthorized : BillingResult;
        public sealed record Failed(string Message) : BillingResult;
    }

    private static async Task<BillingResult> FetchBilling(string token, CancellationToken ct)
    {
        // Both calls go out together; neither Get throws, so awaiting them in
        // sequence can't strand the other request.
        var weeklyCall = Get(BillingBase + "?format=credits", token, ct);
        var monthlyCall = Get(BillingBase, token, ct);
        var weekly = await weeklyCall;
        var monthly = await monthlyCall;

        if (weekly is HttpOutcome.Transport transport) return new BillingResult.Failed(transport.Message);
        if (weekly is not HttpOutcome.Response response) return new BillingResult.Failed("parse error");
        if (response.Status is 401 or 403) return new BillingResult.Unauthorized();
        if (response.Status != 200) return new BillingResult.Failed($"http {response.Status}");
        if (GrokBillingParser.ParseWeekly(response.Body) is not { } pool)
        {
            return new BillingResult.Failed("parse error");
        }

        // Monthly is best-effort garnish — a failure only costs the dollar
        // caption, never the weekly bar.
        var budget = monthly is HttpOutcome.Response { Status: 200 } monthlyResponse
            ? GrokBillingParser.ParseMonthly(monthlyResponse.Body)
            : null;

        return new BillingResult.Success(new GrokBillingSnapshot(
            pool.UsedPercent,
            pool.PeriodEnd,
            budget?.UsedCents,
            budget?.LimitCents,
            budget?.PeriodEnd,
            pool.Products.Count == 0 ? null : pool.Products));
    }

    private abstract record HttpOutcome
    {
        public sealed record Response(int Status, byte[] Body) : HttpOutcome;
        public sealed record Transport(string Message) : HttpOutcome;
    }

    private static async Task<HttpOutcome> Get(string url, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("x-xai-token-auth", "xai-grok-cli");
            request.Headers.TryAddWithoutValidation("x-grok-client-version", ClientVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            // Never let a wedged tunnel hang the poll loop.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            using var response = await Http.Client.SendAsync(request, deadline.Token);
            return new HttpOutcome.Response(
                (int)response.StatusCode,
                await response.Content.ReadAsByteArrayAsync(deadline.Token));
        }
        catch (Exception error)
        {
            return new HttpOutcome.Transport(
                error is HttpRequestException or OperationCanceledException
                    ? L10n.Tr("network drop")
                    : error.Message);
        }
    }

    // MARK: - Token refresh

    /// Exchange the refresh token at the issuer's token endpoint. The response
    /// rotates the refresh token, so success is defined as "the writeback
    /// landed" — an in-memory-only rotation would strand the user's own `grok`
    /// CLI login the next time it refreshes.
    private static async Task<GrokAuthEntry?> Refresh(
        GrokAuthEntry entry, string authPath, CancellationToken ct)
    {
        if (entry.RefreshToken is not { } refreshToken) return null;
        if (entry.OidcClientId is not { } clientId) return null;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, await TokenEndpoint(entry.OidcIssuer, ct));
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Content = new StringContent(
                FormEncoded(
                    ("grant_type", "refresh_token"),
                    ("refresh_token", refreshToken),
                    ("client_id", clientId)),
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Http.Client.SendAsync(request, deadline.Token);
            if ((int)response.StatusCode != 200) return null;
            return GrokAuthFile.ApplyRefreshResponse(
                await response.Content.ReadAsStringAsync(deadline.Token), authPath, entry.Scope);
        }
        catch
        {
            return null;
        }
    }

    /// OIDC discovery for the token endpoint, with the conventional
    /// `/oauth2/token` path as the offline fallback.
    private static async Task<string> TokenEndpoint(string issuer, CancellationToken ct)
    {
        var origin = issuer.TrimEnd('/');
        var fallback = origin + "/oauth2/token";
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, origin + "/.well-known/openid-configuration");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http.Client.SendAsync(request, deadline.Token);
            if ((int)response.StatusCode != 200) return fallback;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            var endpoint = Jsonl.GetString(doc.RootElement, "token_endpoint");
            return Uri.TryCreate(endpoint, UriKind.Absolute, out _) ? endpoint! : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// Uri.EscapeDataString leaves exactly the RFC 3986 unreserved set alone,
    /// which is the encoding the CLI's own form encoder produces.
    private static string FormEncoded(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
}
