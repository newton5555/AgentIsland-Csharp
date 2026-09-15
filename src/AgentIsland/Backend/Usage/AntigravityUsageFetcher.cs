using AgentIsland.Providers.Usage.Antigravity;

namespace AgentIsland.Backend.Usage;

/// Antigravity quota fetch: local language server only. The Google cloud
/// endpoints other tools document are a verified dead end (see
/// AntigravityLanguageServer's header), so there is no token refresh, no
/// OAuth plumbing, and no network beyond loopback here.
public static class AntigravityUsageFetcher
{
    public abstract record Outcome
    {
        /// Buckets plus identity. Email/tier ride along so the store never
        /// needs a second call.
        public sealed record Success(
            AntigravityQuotaSnapshot Snapshot, string? Email) : Outcome;

        /// Antigravity is installed but no process is running — the embedded
        /// server is the only quota source, so there is nothing to read
        /// until agy (or the IDE) starts.
        public sealed record NotRunning : Outcome;

        public sealed record NotInstalled : Outcome;

        public sealed record Failed(string Message) : Outcome;
    }

    public static async Task<Outcome> Fetch(CancellationToken cancellationToken = default)
    {
        if (!AntigravityCredentials.Detected) return new Outcome.NotInstalled();

        var endpoint = await AntigravityLanguageServer.Discover(cancellationToken).ConfigureAwait(false);
        if (endpoint is null) return new Outcome.NotRunning();

        var summary = await AntigravityLanguageServer.Call(
            "RetrieveUserQuotaSummary", endpoint.Port, "{}", endpoint.CsrfToken, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // If the call failed or returned non-200 (e.g. server restarted or token expired),
        // invalidate cache and try rediscovery once.
        if (summary is null || summary.Status != 200)
        {
            AntigravityLanguageServer.InvalidateCache();
            endpoint = await AntigravityLanguageServer.Discover(cancellationToken).ConfigureAwait(false);
            if (endpoint is null) return new Outcome.NotRunning();
            summary = await AntigravityLanguageServer.Call(
                "RetrieveUserQuotaSummary", endpoint.Port, "{}", endpoint.CsrfToken, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (summary is null) return new Outcome.Failed("quota call failed");
        if (summary.Status != 200)
        {
            AntigravityLanguageServer.InvalidateCache();
            return new Outcome.NotRunning();
        }
        if (AntigravityQuotaParser.ParseQuotaSummary(summary.Body) is not { } parsed)
        {
            return new Outcome.Failed("quota reply unreadable");
        }

        // Identity is garnish — a failed status call must not sink the
        // buckets that already arrived.
        AntigravityQuotaParser.UserProfile? profile = null;
        var status = await AntigravityLanguageServer.Call(
            "GetUserStatus", endpoint.Port, "{}", endpoint.CsrfToken, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (status is { Status: 200 })
        {
            profile = AntigravityQuotaParser.ParseUserStatus(status.Body);
        }

        return new Outcome.Success(
            new AntigravityQuotaSnapshot(
                parsed.Buckets,
                profile?.TierId,
                profile?.TierLabel,
                parsed.Note),
            profile?.Email);
    }
}
