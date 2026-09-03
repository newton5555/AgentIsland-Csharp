using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AgentIsland.Localization;

namespace AgentIsland.Usage;

/// In-app Claude Code login using the same PKCE + loopback-callback flow the
/// `claude` CLI uses for a local sign-in. We open the real authorize page in
/// the user's default browser (so their existing claude.ai session is reused —
/// usually one click), and catch the OAuth redirect on a short-lived local
/// HTTP server, so the whole thing completes without a terminal window and
/// without the user copy-pasting a code back. On success the fresh,
/// fully-scoped token pair is written to `.claude\.credentials.json` via
/// `ClaudeCredentials`, which is exactly what a `claude /login` would have
/// done. Direct port of the macOS ClaudeWebLogin (NWListener → HttpListener).
public sealed class ClaudeWebLogin
{
    public static ClaudeWebLogin Shared { get; } = new();
    private ClaudeWebLogin() { }

    public abstract record Outcome
    {
        public sealed record Success : Outcome;
        public sealed record Failed(string Reason) : Outcome;
    }

    /// Ten minutes. The old 180s assumed a browser already signed in to
    /// claude.ai and a single Authorize click; a fresh sign-in through an
    /// emailed code takes minutes, and the timer expiring mid-login tore
    /// down the listener before the redirect ever landed (owner repro,
    /// 2026-08-08). The same ceiling covers the copy-the-link-into-another-
    /// browser detour, so there is no second, longer timeout to arm.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private int _running;

    /// Runs the full flow and resolves once the browser round-trip completes,
    /// times out (~10 min), or fails to start. Safe to call again afterwards.
    /// `onAuthorizeUrl` fires the moment the URL exists, on a thread-pool
    /// thread — the caller marshals it onto the dispatcher and surfaces it as
    /// a copyable link for users whose Claude session lives in a browser
    /// profile the default-browser open won't reach.
    public async Task<Outcome> Start(Action<string>? onAuthorizeUrl = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new Outcome.Failed("login already in progress");
        }
        try
        {
            return await Run(onAuthorizeUrl);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static async Task<Outcome> Run(Action<string>? onAuthorizeUrl)
    {
        var verifier = RandomUrlSafe(32);
        var state = RandomUrlSafe(16);
        var challenge = PkceChallenge(verifier);

        var listener = new HttpListener();
        string redirectUri;
        try
        {
            var port = FreeLoopbackPort();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            redirectUri = $"http://localhost:{port}/callback";
        }
        catch (Exception error)
        {
            // The finally below belongs to the SECOND try, which we never
            // enter — without this close, every failed bind strands an
            // HTTP.SYS request queue for the life of the process.
            try { listener.Close(); } catch { }
            return new Outcome.Failed($"could not open local callback server: {error.Message}");
        }

        try
        {
            var authorizeUrl = BuildAuthorizeUrl(challenge, state, redirectUri);
            onAuthorizeUrl?.Invoke(authorizeUrl);
            if (!OpenInBrowser(authorizeUrl))
            {
                return new Outcome.Failed("could not open the browser");
            }

            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return new Outcome.Failed("login timed out");
                var contextTask = listener.GetContextAsync();
                if (await Task.WhenAny(contextTask, Task.Delay(remaining)) != contextTask)
                {
                    // Abandoning the pending accept; Stop() in finally faults
                    // it, so observe the exception before it goes unhandled.
                    _ = contextTask.ContinueWith(
                        task => _ = task.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                    return new Outcome.Failed("login timed out");
                }
                var context = await contextTask;

                if (context.Request.Url?.AbsolutePath != "/callback")
                {
                    // Favicon or an unrelated probe — answer politely, keep waiting.
                    await Respond(context.Response, ok: false);
                    continue;
                }
                var query = context.Request.QueryString;
                if (query["error"] is { Length: > 0 } error)
                {
                    await Respond(context.Response, ok: false);
                    return new Outcome.Failed(error);
                }
                var code = query["code"];
                if (string.IsNullOrEmpty(code) || query["state"] != state)
                {
                    await Respond(context.Response, ok: false);
                    return new Outcome.Failed("state mismatch");
                }

                var ok = await ClaudeCredentials.CompleteWebLogin(code!, verifier, redirectUri, state);
                await Respond(context.Response, ok);
                return ok ? new Outcome.Success() : new Outcome.Failed("token exchange failed");
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }

    /// The one authorize-URL builder, shared by the loopback flow and the
    /// paste flow — they differ only in `redirectUri`. Every parameter here
    /// was captured verbatim from what the official `claude` CLI 2.1.153
    /// opens; nothing is inferred.
    ///
    /// `code=true` is not decoration: it rides BOTH of the CLI's variants,
    /// and the authorize SUBMIT (not the consent page render) hard-fails with
    /// "Invalid request format" without it. Verified live on 2026-07-11; an
    /// earlier theory called it a paste-flow selector and dropped it, wrongly.
    internal static string BuildAuthorizeUrl(string challenge, string state, string redirectUri) =>
        ClaudeCredentials.AuthorizeUrlBase
            + "?code=true"
            + "&client_id=" + Uri.EscapeDataString(ClaudeCredentials.OauthClientId)
            + "&response_type=code"
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&scope=" + Uri.EscapeDataString(ClaudeCredentials.LoginScopes)
            + "&code_challenge=" + Uri.EscapeDataString(challenge)
            + "&code_challenge_method=S256"
            + "&state=" + Uri.EscapeDataString(state);

    /// Hands the URL to the default browser. Shared with the paste flow so
    /// both sign-in paths open pages the same way.
    internal static bool OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// The callback page shown in the browser — same dark card and copy as
    /// the macOS version. Localized, not hardcoded Chinese: this page leaked
    /// Chinese into the English UI once already (owner report, 1.7.2).
    private static async Task Respond(HttpListenerResponse response, bool ok)
    {
        var emoji = ok ? "✅" : "⚠️";
        var title = L10n.Tr(ok ? "Connected to Claude" : "Login incomplete");
        var note = L10n.Tr(ok
            ? "Authentication succeeded — close this page and return to Agent Island"
            : "Please return to Agent Island and try again");
        var body = "<!doctype html><html><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Agent Island</title></head>"
            + "<body style=\"margin:0;height:100vh;display:flex;align-items:center;justify-content:center;"
            + "background:#0b0f0e;color:#e8f0ee;font-family:-apple-system,system-ui,sans-serif\">"
            + "<div style=\"text-align:center\"><div style=\"font-size:46px;margin-bottom:14px\">" + emoji + "</div>"
            + "<div style=\"font-size:19px;font-weight:600\">" + title + "</div>"
            + "<div style=\"margin-top:8px;color:#8a9a95\">" + note + "</div></div></body></html>";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.OutputStream.Close();
        }
        catch
        {
            // Browser hung up early — the flow outcome doesn't depend on it.
        }
    }

    /// A port the OS considers free right now. The tiny bind race between
    /// probing and HttpListener.Start is the same one the CLI's own loopback
    /// flow accepts.
    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    // MARK: - PKCE helpers

    /// S256 challenge for a verifier — shared with the paste flow. The
    /// verifier is base64url text, so ASCII bytes are the same bytes UTF-8
    /// would produce.
    internal static string PkceChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static string RandomUrlSafe(int bytes) =>
        Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
