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

        var port = await AntigravityLanguageServer.Discover(cancellationToken).ConfigureAwait(false);
        if (port is not { } livePort) return new Outcome.NotRunning();

        var summary = await CallWithCsrfRetry(
            "RetrieveUserQuotaSummary", livePort, "{}", cancellationToken).ConfigureAwait(false);
        if (summary is null) return new Outcome.Failed("quota call failed");
        if (summary.Status != 200)
        {
            return new Outcome.Failed($"quota HTTP {summary.Status}");
        }
        if (AntigravityQuotaParser.ParseQuotaSummary(summary.Body) is not { } parsed)
        {
            return new Outcome.Failed("quota reply unreadable");
        }

        // Identity is garnish — a failed status call must not sink the
        // buckets that already arrived.
        AntigravityQuotaParser.UserProfile? profile = null;
        if (await CallWithCsrfRetry("GetUserStatus", livePort, "{}", cancellationToken).ConfigureAwait(false)
            is { Status: 200 } status)
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

    /// The CLI's server wants no CSRF token; the desktop IDE gates on one it
    /// passes on its own command line. The token is lifted only after a 401
    /// says the plain call was refused — a strictly additive retry on a path
    /// that already failed.
    private static async Task<AntigravityLanguageServer.Reply?> CallWithCsrfRetry(
        string method, int port, string body, CancellationToken cancellationToken)
    {
        var reply = await AntigravityLanguageServer.Call(
            method, port, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reply is not { Status: 401 }) return reply;
        foreach (var pid in AntigravityLanguageServer.AntigravityProcessIds())
        {
            if (AntigravityLanguageServer.CsrfToken(pid) is not { Length: > 0 } token) continue;
            var retried = await AntigravityLanguageServer
                .Call(method, port, body, token, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (retried is { Status: 200 }) return retried;
        }
        return reply;
    }
}
