using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// One selected login entry from the Grok CLI credential store.
public sealed record GrokAuthEntry(
    string Scope,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? AuthMode,
    string? Email,
    string OidcIssuer,
    string? OidcClientId)
{
    public bool IsSuperGrok => string.Equals(AuthMode, "oidc", StringComparison.OrdinalIgnoreCase);
}

/// Reader/rotator for %USERPROFILE%\.grok\auth.json. Top-level keys are OIDC
/// scope URLs — "https://auth.x.ai::&lt;client-id&gt;" for SuperGrok logins, with
/// the client id riding after the "::". `expires_at` is epoch milliseconds.
///
/// The token endpoint rotates the refresh token on every exchange, so a
/// successful refresh MUST be written back — losing the new refresh token
/// strands the user's own `grok` CLI login. The writeback re-reads the file
/// first, so every field this app doesn't understand survives verbatim, and
/// lands atomically (sibling temp file + File.Move overwrite) so a crash
/// mid-write can never leave a half-written credential file behind.
public static class GrokAuthFile
{
    public const string DefaultIssuer = "https://auth.x.ai";
    private const string OidcScopeMarker = "auth.x.ai";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// GROK_HOME overrides the credential directory, matching the CLI. A
    /// tilde-prefixed value is expanded here: Windows shells never do it, and
    /// the variable travels inside dotfiles copied over from a Mac.
    public static string DefaultPath => Path.Combine(GrokHome, "auth.json");

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static GrokAuthEntry? LoadEntry(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return null;
            using var stream = OpenShared(file);
            if (JsonNode.Parse(stream) is not JsonObject root) return null;
            if (SelectEntry(root) is not { } selected) return null;
            return Entry(selected.Scope, selected.Raw);
        }
        catch
        {
            return null;
        }
    }

    /// Refresh a minute early so an in-flight request never races the clock.
    public static bool NeedsRefresh(GrokAuthEntry entry, DateTimeOffset? now = null, TimeSpan? skew = null)
    {
        if (entry.ExpiresAt is not { } expiresAt) return false;
        return (now ?? DateTimeOffset.Now) + (skew ?? TimeSpan.FromSeconds(60)) >= expiresAt;
    }

    /// Apply an OAuth token response (`access_token` / rotated
    /// `refresh_token` / `expires_in` seconds) to the entry at `scope` and
    /// persist it. Returns the updated entry, or null when nothing was
    /// written (malformed response, missing file or scope, write failure) —
    /// the caller then keeps using its in-memory token.
    public static GrokAuthEntry? ApplyRefreshResponse(
        string responseJson, string path, string scope, DateTimeOffset? now = null)
    {
        try
        {
            if (JsonNode.Parse(responseJson) is not JsonObject response) return null;
            if (NonEmpty(response["access_token"]) is not { } accessToken) return null;

            JsonObject root;
            using (var stream = OpenShared(path))
            {
                if (JsonNode.Parse(stream) is not JsonObject parsed) return null;
                root = parsed;
            }
            if (root[scope] is not JsonObject raw) return null;

            raw["key"] = accessToken;
            if (NonEmpty(response["refresh_token"]) is { } rotated) raw["refresh_token"] = rotated;
            if (Number(response["expires_in"]) is { } expiresIn && expiresIn > 0)
            {
                raw["expires_at"] = (now ?? DateTimeOffset.Now).AddSeconds(expiresIn).ToUnixTimeMilliseconds();
            }

            if (!WriteAtomically(path, root)) return null;
            return Entry(scope, raw);
        }
        catch
        {
            return null;
        }
    }

    // MARK: - Entry selection

    /// Prefer "auth.x.ai" scopes (the SuperGrok OIDC login); among candidates
    /// prefer non-expired, then latest expiry, then stable scope order.
    /// Legacy session scopes are a last resort.
    private static (string Scope, JsonObject Raw)? SelectEntry(JsonObject root)
    {
        var oidc = new List<(string Scope, JsonObject Raw)>();
        var legacy = new List<(string Scope, JsonObject Raw)>();
        foreach (var (scope, value) in root)
        {
            if (value is not JsonObject raw || NonEmpty(raw["key"]) is null) continue;
            if (scope.Contains(OidcScopeMarker, StringComparison.Ordinal)) oidc.Add((scope, raw));
            else legacy.Add((scope, raw));
        }
        return Best(oidc) ?? Best(legacy);
    }

    private static (string Scope, JsonObject Raw)? Best(List<(string Scope, JsonObject Raw)> candidates)
    {
        if (candidates.Count == 0) return null;
        var now = DateTimeOffset.Now;
        return candidates
            // A missing expiry counts as "never expires" for the liveness
            // test but sorts last on recency — an undated entry should not
            // outrank a freshly minted one.
            .OrderBy(candidate => (ParseExpiry(candidate.Raw["expires_at"]) ?? DateTimeOffset.MaxValue) <= now)
            .ThenByDescending(candidate => ParseExpiry(candidate.Raw["expires_at"]) ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Scope, StringComparer.Ordinal)
            .First();
    }

    private static GrokAuthEntry? Entry(string scope, JsonObject raw)
    {
        if (NonEmpty(raw["key"]) is not { } accessToken) return null;
        return new GrokAuthEntry(
            scope,
            accessToken,
            NonEmpty(raw["refresh_token"]),
            ParseExpiry(raw["expires_at"]),
            NonEmpty(raw["auth_mode"]),
            NonEmpty(raw["email"]),
            NonEmpty(raw["oidc_issuer"]) ?? DefaultIssuer,
            NonEmpty(raw["oidc_client_id"]) ?? ClientIdFromScope(scope));
    }

    private static string? ClientIdFromScope(string scope)
    {
        var marker = scope.IndexOf("::", StringComparison.Ordinal);
        if (marker < 0) return null;
        var id = scope[(marker + 2)..];
        return id.Length == 0 ? null : id;
    }

    /// `expires_at` is epoch milliseconds on current CLI logins; tolerate
    /// epoch seconds and ISO strings from other writer versions.
    public static DateTimeOffset? ParseExpiry(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (Number(node) is { } raw) return FromEpochMilliseconds(raw > 1e11 ? raw : raw * 1000);
        return value.TryGetValue<string>(out var text) ? GrokTimestamp.Parse(text) : null;
    }

    // MARK: - Write-back

    /// The temp file is created in the SAME directory as auth.json, so it
    /// inherits the identical ACL — the closest Windows equivalent of the
    /// 0600 the macOS writer sets, since the user profile tree is already
    /// user-only and File.Move keeps that inheritance at the destination.
    private static bool WriteAtomically(string path, JsonObject root)
    {
        if (Path.GetDirectoryName(path) is not { Length: > 0 } directory) return false;
        var tmp = Path.Combine(directory, ".auth.json.tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    // The grok CLI may hold a brief read lock on auth.json,
                    // and dropping the rotated refresh token bricks its login.
                    System.Threading.Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: failed to persist rotated Grok tokens: {error.Message}");
            TryDelete(tmp);
            return false;
        }
    }

    /// The grok CLI keeps auth.json open while it runs; the default
    /// File.ReadAll* share mode throws against that, and a failed read here
    /// reads to the caller as "grok was never signed in on this machine",
    /// which blanks the whole row.
    private static FileStream OpenShared(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stranded temp file is noise, not a failure worth surfacing.
        }
    }

    // MARK: - Paths and value coercion

    private static string GrokHome
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("GROK_HOME");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(IslandPaths.Home, ".grok")
                : ExpandHome(custom);
        }
    }

    private static string ExpandHome(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed == "~") return IslandPaths.Home;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal)
            || trimmed.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Path.Combine(IslandPaths.Home, trimmed[2..]);
        }
        return trimmed;
    }

    private static string? NonEmpty(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (!value.TryGetValue<string>(out var text)) return null;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// Numbers arrive either element-backed (parsed from the file) or as the
    /// long this app just assigned, so both backings are probed before the
    /// string fallback older writers used.
    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static DateTimeOffset? FromEpochMilliseconds(double milliseconds)
    {
        if (double.IsNaN(milliseconds)
            || milliseconds < -62_135_596_800_000d
            || milliseconds > 253_402_300_799_999d)
        {
            return null;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }
}
