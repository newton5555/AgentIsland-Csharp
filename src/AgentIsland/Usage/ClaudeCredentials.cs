using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Deep module owning Claude OAuth credential acquisition: the
/// env -&gt; credentials-file -&gt; refresh -&gt; rotation-writeback flow, plus the
/// in-app re-auth helpers. On macOS the store is the login Keychain; on
/// Windows, Claude Code persists the same claudeAiOauth JSON at
/// %USERPROFILE%\.claude\.credentials.json.
///
/// The asymmetry between token sources is load-bearing:
///   - An env-token scope-insufficient (403) does NOT short-circuit; we
///     still try the stored token.
///   - A stored-token (or refreshed-token) scope-insufficient short-circuits
///     to re-auth, because refresh re-issues the same scope set and cannot
///     recover a missing `user:profile`.
public static class ClaudeCredentials
{
    /// The UI layer matches on this exact string to swap the error caption
    /// for an in-app re-auth button.
    public const string ReauthRequiredMessage = "re-login: claude /login";

    public const string AuthRequiredMessage = "auth required — run claude";

    /// The stored token 401'd AND the refresh grant failed. Distinct from the
    /// cold-start message on purpose: without it the caption stayed on
    /// "auth required — run claude" forever, so a revoked refresh token was
    /// indistinguishable from never having signed in (macOS #22).
    public const string TokenRefreshFailedMessage = "token refresh failed — sign in again";

    public static bool IsAuthRecoverableError(string? message) =>
        message is ReauthRequiredMessage or AuthRequiredMessage or TokenRefreshFailedMessage;

    // OAuth constants — captured verbatim from what the official `claude`
    // CLI 2.1.153 opens, and shared with the macOS ClaudeCredentials.
    public const string OauthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// Where the browser goes to sign in.
    ///
    /// This MUST be the claude.com/cai (SUBSCRIPTION) authorize base, not the
    /// Console one. `platform.claude.com` is the DEVELOPER CONSOLE — the
    /// API-key product with its own separate account system; sending a
    /// Max/Pro subscriber there lands them on a sign-up form and no
    /// authorization code is ever issued. The CLI keeps both bases and picks
    /// this one for a subscription login.
    public const string AuthorizeUrlBase = "https://claude.com/cai/oauth/authorize";

    /// Token endpoint for both refresh and authorization_code exchange.
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";

    /// Anthropic's own paste-flow redirect. The pairing with
    /// `AuthorizeUrlBase` is deliberately CROSS-origin — authorize lives on
    /// claude.com/cai while the code-display page lives on
    /// platform.claude.com. Both URLs sit verbatim in the claude CLI 2.1.153
    /// binary, which is the ground truth here.
    public const string PasteRedirectUri = "https://platform.claude.com/oauth/code/callback";

    /// EXACTLY the six scopes `claude /login` sends, captured verbatim by
    /// intercepting the authorize URL the running CLI opens (2026-08-08).
    /// An earlier round cut this to two from `setup-token` — a DIFFERENT
    /// flow with a smaller scope set — and the subscription /login the app
    /// mimics then failed as "Invalid request format" AFTER sign-in for
    /// everyone using the in-app button. This is the exact string, order
    /// included.
    public const string LoginScopes = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    public abstract record ProbeOutcome
    {
        public sealed record Success(AppUsage Usage) : ProbeOutcome;
        public sealed record RateLimited : ProbeOutcome;
        public sealed record Unauthorized : ProbeOutcome;
        /// Token is structurally valid but missing a scope the server now
        /// requires (`user:profile`, added mid-2026). Refresh won't help.
        public sealed record ScopeInsufficient : ProbeOutcome;
        public sealed record OtherError(string Message) : ProbeOutcome;
    }

    public abstract record Resolution
    {
        public sealed record Usage(AppUsage Value) : Resolution;
        public sealed record ReauthRequired(string Message) : Resolution;
        public sealed record Failed(string Message) : Resolution;
    }

    // MARK: - Resolution

    /// Token sources, in order of freshness:
    ///   1. CLAUDE_CODE_OAUTH_TOKEN — set by Claude Desktop for child
    ///      processes; always fresh while Desktop is running.
    ///   2. ~/.claude/.credentials.json — written by `claude /login`; the
    ///      access token expires after ~8h, after which we refresh.
    ///   3. platform.claude.com/v1/oauth/token refresh — Anthropic rotates
    ///      the refresh_token on every call; the new pair MUST be persisted
    ///      back or Claude Code itself 401s on its next refresh.
    public static async Task<Resolution> ResolveUsage(Func<string, string?, Task<ProbeOutcome>> probe)
    {
        var lastError = AuthRequiredMessage;
        var cachedCreds = ReadClaudeCreds();
        var plan = cachedCreds?.SubscriptionType;

        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
        {
            switch (await probe(envToken, plan))
            {
                case ProbeOutcome.Success success: return new Resolution.Usage(success.Usage);
                case ProbeOutcome.RateLimited: lastError = "rate limited"; break;
                case ProbeOutcome.Unauthorized: break;
                case ProbeOutcome.ScopeInsufficient: lastError = ReauthRequiredMessage; break;
                case ProbeOutcome.OtherError error: lastError = error.Message; break;
            }
        }

        if (cachedCreds is { } creds)
        {
            switch (await probe(creds.AccessToken, plan))
            {
                case ProbeOutcome.Success success: return new Resolution.Usage(success.Usage);
                // A 429 is rate limiting, not an expired token — refreshing
                // wouldn't help and only doubles load on an already-limited
                // endpoint (and needlessly rotates the refresh token).
                case ProbeOutcome.RateLimited: return new Resolution.Failed("rate limited");
                // Refresh hands back tokens with the same scope set, so it
                // cannot recover from a missing-scope 403.
                case ProbeOutcome.ScopeInsufficient: return new Resolution.ReauthRequired(ReauthRequiredMessage);
                case ProbeOutcome.OtherError error: lastError = error.Message; break;
                case ProbeOutcome.Unauthorized:
                    // Only a 401 means the access token expired; refresh it.
                    if (await RefreshClaudeToken(creds.RefreshToken) is { } refreshed)
                    {
                        // Anthropic rotates the refresh token, invalidating the
                        // pair we just used. If we can't persist the rotated
                        // tokens, the on-disk creds are now dead — force a
                        // re-login instead of 401-ing forever.
                        if (!WriteClaudeCreds(refreshed))
                        {
                            return new Resolution.ReauthRequired(ReauthRequiredMessage);
                        }
                        switch (await probe(refreshed.AccessToken, plan))
                        {
                            case ProbeOutcome.Success success: return new Resolution.Usage(success.Usage);
                            case ProbeOutcome.RateLimited: return new Resolution.Failed("rate limited");
                            case ProbeOutcome.ScopeInsufficient: return new Resolution.ReauthRequired(ReauthRequiredMessage);
                            case ProbeOutcome.OtherError error2: lastError = error2.Message; break;
                            case ProbeOutcome.Unauthorized: break;
                        }
                    }
                    else
                    {
                        lastError = TokenRefreshFailedMessage;
                    }
                    break;
            }
        }

        return new Resolution.Failed(lastError);
    }

    // MARK: - Credentials file

    private sealed record ClaudeCreds(
        string AccessToken,
        string RefreshToken,
        string? SubscriptionType);

    private static ClaudeCreds? ReadClaudeCreds()
    {
        try
        {
            var path = IslandPaths.ClaudeCredentialsFile;
            if (!File.Exists(path)) return null;
            using var stream = OpenShared(path);
            using var doc = JsonDocument.Parse(stream);
            if (Jsonl.GetObject(doc.RootElement, "claudeAiOauth") is not { } oauth) return null;
            var access = Jsonl.GetString(oauth, "accessToken");
            var refresh = Jsonl.GetString(oauth, "refreshToken");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return null;
            return new ClaudeCreds(access!, refresh!, Jsonl.GetString(oauth, "subscriptionType"));
        }
        catch
        {
            return null;
        }
    }

    /// Updates the claudeAiOauth block in place, preserving unrelated fields
    /// (scopes, subscriptionType, rateLimitTier) and any other top-level
    /// keys in the file; seeds a minimal structure when the file doesn't
    /// exist yet (a fresh web login on a machine that never ran the CLI).
    /// Returns false if the write ultimately fails — Anthropic has already
    /// invalidated the OLD refresh token server-side, so a lost write means
    /// the on-disk credentials are dead and the caller must force a re-login
    /// rather than 401 forever.
    ///
    /// There is no chmod 0600 to mirror here: the file lives inside
    /// %USERPROFILE%, and both the temp file and the destination inherit that
    /// tree's per-user DACL — the same protection Claude Code's own writer
    /// gets on Windows.
    private static bool WriteClaudeCreds(RefreshedTokens refreshed)
    {
        var tmp = string.Empty;
        try
        {
            var path = IslandPaths.ClaudeCredentialsFile;
            JsonNode root;
            try
            {
                // Share the read: Claude Code and Claude Desktop keep this file
                // open, and an exclusive open would throw — dropping us into the
                // empty-object branch, which rewrites the file WITHOUT scopes,
                // subscriptionType or anything else the CLI stores there.
                using var existing = OpenShared(path);
                root = JsonNode.Parse(existing) ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
            if (root["claudeAiOauth"] is not JsonObject oauth)
            {
                oauth = new JsonObject();
                root["claudeAiOauth"] = oauth;
            }
            oauth["accessToken"] = refreshed.AccessToken;
            oauth["refreshToken"] = refreshed.RefreshToken;
            oauth["expiresAt"] = refreshed.ExpiresAtMs;

            // A fresh web login may land on a machine that never ran the CLI,
            // where ~\.claude doesn't exist yet.
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            // Per-write temp name, not a fixed ".tmp": Windows denies a second
            // opener while the first holds the handle, so two Agent Island
            // instances refreshing at once would fail each other's write —
            // and a failed write here forces the user through a re-login.
            tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            // Retry the rename: Claude Code / Claude Desktop may hold a brief
            // read lock on the creds file, and dropping the rotated token
            // bricks auth for every consumer.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    System.Threading.Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"AgentIsland: failed to write rotated Claude tokens: {error.Message}");
            // Never strand the temp copy: it holds a live token pair, and
            // nothing else ever cleans up ~\.claude.
            try { if (tmp.Length > 0) File.Delete(tmp); } catch { }
            return false;
        }
    }

    /// Read handle that tolerates the writer still holding the file. Claude
    /// Code and Claude Desktop keep .credentials.json open while they run, and
    /// the default File.Read* share mode throws a sharing violation against
    /// them — which this class would otherwise read as "not signed in".
    private static FileStream OpenShared(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// Cheap local change signal used by the re-auth poll — file mtime here,
    /// keychain `mdat` on macOS.
    public static string? CredentialsModificationStamp()
    {
        try
        {
            var path = IslandPaths.ClaudeCredentialsFile;
            if (!File.Exists(path)) return null;
            return File.GetLastWriteTimeUtc(path).Ticks.ToString();
        }
        catch
        {
            return null;
        }
    }

    // MARK: - Refresh

    private sealed record RefreshedTokens(string AccessToken, string RefreshToken, long ExpiresAtMs);

    private static async Task<RefreshedTokens?> RefreshClaudeToken(string refreshToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = OauthClientId,
            });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.Client.SendAsync(request);
            if ((int)response.StatusCode != 200) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var access = Jsonl.GetString(doc.RootElement, "access_token");
            var refresh = Jsonl.GetString(doc.RootElement, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return null;
            var expiresIn = Jsonl.GetDouble(doc.RootElement, "expires_in") ?? 28_800;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
            return new RefreshedTokens(access!, refresh!, expiresAt);
        }
        catch
        {
            return null;
        }
    }

    // MARK: - Web login (PKCE loopback)

    /// Second half of the loopback login: exchange the authorization code for
    /// a fresh token pair and persist it to the credentials file. Called by
    /// `ClaudeWebLogin` after it catches the OAuth redirect. Returns true only
    /// if both the exchange and the file write succeed.
    public static async Task<bool> CompleteWebLogin(
        string code, string codeVerifier, string redirectUri, string state)
    {
        if (await ExchangeAuthorizationCode(code, codeVerifier, redirectUri, state) is not { } tokens)
        {
            return false;
        }
        return WriteClaudeCreds(tokens);
    }

    /// Mirrors `RefreshClaudeToken` but with grant_type=authorization_code,
    /// for the very first token pair from a fresh browser sign-in.
    private static async Task<RefreshedTokens?> ExchangeAuthorizationCode(
        string code, string codeVerifier, string redirectUri, string state)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["client_id"] = OauthClientId,
                ["redirect_uri"] = redirectUri,
                ["state"] = state,
            });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.Client.SendAsync(request);
            if ((int)response.StatusCode != 200) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var access = Jsonl.GetString(doc.RootElement, "access_token");
            var refresh = Jsonl.GetString(doc.RootElement, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) return null;
            // expires_in is seconds; Claude Code stores absolute ms.
            var expiresIn = Jsonl.GetDouble(doc.RootElement, "expires_in") ?? 28_800;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
            return new RefreshedTokens(access!, refresh!, expiresAt);
        }
        catch
        {
            return null;
        }
    }

    // MARK: - Paste-code login

    /// What a paste sign-in needs: the page to open, plus the PKCE verifier
    /// and state to hand back to `CompletePasteLogin`.
    public sealed record PasteLoginTicket(string Url, string Verifier, string State);

    /// Builds the paste-flow authorize URL and its PKCE pair. The caller
    /// opens the URL, the user approves and copies the code Anthropic's page
    /// shows, then the caller passes that code to `CompletePasteLogin`. No
    /// loopback listener and no browser-profile guessing — this is the escape
    /// hatch for the machines where the round-trip cannot finish.
    public static PasteLoginTicket BeginPasteLogin()
    {
        var verifier = ClaudeWebLogin.RandomUrlSafe(32);
        var state = ClaudeWebLogin.RandomUrlSafe(16);
        var challenge = ClaudeWebLogin.PkceChallenge(verifier);
        return new PasteLoginTicket(
            ClaudeWebLogin.BuildAuthorizeUrl(challenge, state, PasteRedirectUri),
            verifier,
            state);
    }

    /// Exchanges a hand-pasted authorization code and persists the pair it
    /// returns. Anthropic's success page renders the code as
    /// `&lt;code&gt;#&lt;state&gt;`; both that form and a bare code are
    /// accepted.
    public static async Task<bool> CompletePasteLogin(string pasted, string verifier, string state)
    {
        var trimmed = pasted.Trim();
        if (trimmed.Length == 0) return false;
        var separator = trimmed.IndexOf('#');
        var code = separator < 0 ? trimmed : trimmed[..separator];
        if (code.Length == 0) return false;
        // A bare code carries no state back, so echo the one we sent.
        var returnedState = separator < 0 || separator == trimmed.Length - 1
            ? state
            : trimmed[(separator + 1)..];
        if (await ExchangeAuthorizationCode(code, verifier, PasteRedirectUri, returnedState) is not { } tokens)
        {
            return false;
        }
        return WriteClaudeCreds(tokens);
    }

    // MARK: - In-app re-auth
    //
    // There is deliberately no terminal fallback here. `claude auth login` is
    // a retired command on the 2.x CLI, so spawning it after a failed browser
    // round produced an "authentication failed" from nowhere that read as a
    // system dialog (owner repro, 2026-08-08). macOS deleted that path; the
    // recovery is `ClaudeWebLogin.Outcome.Failed.Reason` on the Claude row
    // plus `BeginPasteLogin`, both of which need no CLI at all.
}
