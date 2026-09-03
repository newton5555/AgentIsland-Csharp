using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Cursor's session, read out of the editor's own key/value store
/// (%APPDATA%\Cursor\User\globalStorage\state.vscdb). Cursor keeps a WorkOS
/// session JWT under `cursorAuth/accessToken`; its `sub` claim is the user id
/// every dashboard endpoint keys on.
///
/// macOS opens that file with the system SQLite. Windows ships no SQLite and
/// this project takes no NuGet dependencies, so the values are recovered by
/// scanning the raw bytes instead: ItemTable stores a row's key and value as
/// adjacent text in the record body, which makes `cursorAuth/accessToken`
/// plus the token one contiguous ASCII run. Cursor holds the db open the
/// whole time it runs, so every read shares read/write and a transient
/// failure just means "try again next poll". Display-only: the token is
/// handed straight back to Cursor's own API and never logged.
public static class CursorCredentials
{
    public sealed record Session(string AccessToken, string UserId);

    public static string StateDbPath => Path.Combine(
        IslandPaths.RoamingAppData, "Cursor", "User", "globalStorage", "state.vscdb");

    /// Detection is the state db itself — a bare install with no db has
    /// nothing to show, and no db means no login either way.
    public static bool Exists() => File.Exists(StateDbPath);

    public static Session? ReadSession()
    {
        // Freed SQLite pages and superseded WAL frames keep copies of earlier
        // tokens, and a raw scan can't tell live rows from dead ones the way
        // a SELECT can. The live token is simply the one that expires last.
        Session? best = null;
        var bestExpiry = 0d;
        foreach (var token in Scan().Tokens)
        {
            if (!LooksLikeJwt(token) || DecodeSubject(token) is not { } subject) continue;
            var expiry = ExpiresAt(token);
            if (best is not null && expiry <= bestExpiry) continue;
            best = new Session(token, subject);
            bestExpiry = expiry;
        }
        return best;
    }

    /// The tier as Cursor's own billing store has it locally ("free",
    /// "pro"…). Only the fallback when the plan endpoint can't be reached —
    /// the endpoint is authoritative and this copy can lag a plan change.
    public static string? CachedPlan() => NormalizePlan(Last(Scan().Plans));

    /// Account identity for hover detail only — never the default-visible
    /// caption (owner rule: rows lead with sync state, not an email).
    public static string? CachedEmail() => Last(Scan().Emails);

    // MARK: - Raw scan

    private const int ChunkBytes = 1 << 20;
    /// Carried across chunk seams so no key+value run is ever cut in half.
    /// A WorkOS JWT is ~1 KB; 8 KB leaves room to spare.
    private const int OverlapChars = 8192;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex AccessTokenPattern = new(
        @"cursorAuth/accessToken(eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex MembershipPattern = new(
        @"cursorAuth/stripeMembershipType([A-Za-z_]{3,20})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// Only a strict address shape is accepted: the scan sees no length
    /// prefix to stop at, so a loose pattern would trail bytes of whatever
    /// record happens to follow the value.
    private static readonly Regex EmailPattern = new(
        @"cursorAuth/cachedEmail([A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]{1,63}(?:\.[A-Za-z0-9\-]{1,63})*\.[A-Za-z]{2,12})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// One pass yields all three values. The db routinely runs 50-200 MB, and
    /// a poll asks for token, plan and email, so a pattern-per-pass design
    /// streamed the whole file six times (main + WAL, three times over) for
    /// three values that live in the same bytes.
    private sealed record ScanResult(List<string> Tokens, List<string> Plans, List<string> Emails);

    private static readonly object ScanGate = new();
    private static string? _scanStamp;
    private static ScanResult? _scanned;

    /// Memoised on the (length, mtime) of both files: within one poll the
    /// three accessors share a single read, and an untouched db between polls
    /// costs two stats instead of a full stream.
    private static ScanResult Scan()
    {
        var db = StateDbPath;
        var stamp = Stamp(db) + "|" + Stamp(db + "-wal");
        lock (ScanGate)
        {
            if (_scanned is { } cached && _scanStamp == stamp) return cached;
        }

        var result = new ScanResult(new List<string>(), new List<string>(), new List<string>());
        // The db is Cursor's live working file; a value written moments ago
        // may still be sitting in the WAL sidecar rather than the main file,
        // so both are scanned, sidecar last.
        ScanFile(db, result);
        ScanFile(db + "-wal", result);

        lock (ScanGate)
        {
            _scanStamp = stamp;
            _scanned = result;
        }
        return result;
    }

    private static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "-";
        }
        catch
        {
            // An unreadable stamp must never look like a stable one, or a
            // transient failure would freeze the cache.
            return Guid.NewGuid().ToString("N");
        }
    }

    /// Last wins: WAL frames are appended and the sidecar is scanned after
    /// the main file, so the freshest copy of a rewritten row ends up last.
    /// (The token picks by expiry instead — a stronger signal than position.)
    private static string? Last(List<string> found) => found.Count == 0 ? null : found[^1];

    private static void ScanFile(string path, ScanResult into)
    {
        try
        {
            if (!File.Exists(path)) return;
            // FileShare.ReadWrite is mandatory: Cursor keeps the db open, and
            // an exclusive open throws instead of reading. Chunked so a
            // multi-megabyte db never becomes a multi-megabyte string.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096);
            var buffer = new byte[ChunkBytes];
            var carry = string.Empty;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                // Latin-1 is a lossless byte-to-char map and the patterns are
                // pure ASCII, so the decoder never has to be right about the
                // binary stretches between records.
                var text = carry + Encoding.Latin1.GetString(buffer, 0, read);
                Collect(AccessTokenPattern, text, into.Tokens);
                Collect(MembershipPattern, text, into.Plans);
                Collect(EmailPattern, text, into.Emails);
                carry = text.Length <= OverlapChars ? text : text[^OverlapChars..];
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological file costs this poll's numbers, not the app.
        }
        catch (Exception)
        {
            // Locked, truncated mid-write, or gone: try again next poll.
        }
    }

    private static void Collect(Regex pattern, string text, List<string> found)
    {
        foreach (Match match in pattern.Matches(text))
        {
            var value = match.Groups[1].Value;
            if (value.Length > 0) found.Add(value);
        }
    }

    // MARK: - Pure helpers (unit-tested)

    public static bool LooksLikeJwt(string token) =>
        token.StartsWith("eyJ", StringComparison.Ordinal) && token.Split('.').Length == 3;

    /// The JWT's `sub` claim, e.g. "user_01ABC…". The signature is irrelevant
    /// here: the token is only ever handed back to Cursor, which verifies it.
    /// Payload is base64url with the padding stripped.
    public static string? DecodeSubject(string token)
    {
        if (DecodePayload(token) is not { } payload) return null;
        var subject = Jsonl.GetString(payload, "sub");
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    /// Cursor's known tiers, longest-match-first so "pro_plus" never reads as
    /// "pro". Unknown strings drop rather than show garbage in a chip.
    public static string? NormalizePlan(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var lower = raw.ToLowerInvariant();
        foreach (var tier in Tiers)
        {
            if (lower.StartsWith(tier, StringComparison.Ordinal)) return tier.Replace('_', ' ');
        }
        return null;
    }

    private static readonly string[] Tiers =
    {
        "free_trial", "pro_plus", "enterprise", "business", "ultra", "team", "free", "pro",
    };

    private static double ExpiresAt(string token)
    {
        if (DecodePayload(token) is not { } payload) return 0;
        return Jsonl.GetDouble(payload, "exp") ?? Jsonl.GetDouble(payload, "iat") ?? 0;
    }

    private static JsonElement? DecodePayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            // The document is disposed on return; Clone detaches the element.
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
