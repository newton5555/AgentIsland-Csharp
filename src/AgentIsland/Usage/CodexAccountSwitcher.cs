using System.IO;
using System.Text;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// One-click Codex account switching, ported from the macOS
/// CodexAccountSwitcher (owner call, 2026-08-08).
///
/// Codex keeps exactly one signed-in account in %USERPROFILE%\.codex\auth.json.
/// This parks a COPY of each account's credentials beside it and swaps the
/// live file on demand — the same thing a person does by hand with `move`,
/// minus the mistakes. Two modes:
///   - MANUAL — the user picks from the settings menu, the file swaps.
///   - AUTO (opt-in, off by default) — when the live account's window reads
///     100%, rotate to the next parked account. Modeled on the open-source
///     codex-auto pool tools, but keyed off the real /wham/usage percentages
///     instead of scraping terminal output for failure text, so it never
///     guesses. UsageStore owns the trigger (it sees every fresh reading);
///     this type owns policy + the swap.
public static class CodexAccountSwitcher
{
    /// A parked account: the label the user gave it plus its stored file.
    /// Email is read out of the stored credentials so the picker can show who
    /// a login belongs to; the token itself never leaves the file.
    public sealed record Account(string Label, string Path, string? Email);

    private const string AutoSwitchKey = "codexAutoSwitchWhenExhausted";
    private const int MaxLabelLength = 40;

    /// Reserved DOS device names still resolve as devices in any directory and
    /// with any extension, so a label of "CON" would send the credentials to
    /// the console instead of "CON.json".
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string LivePath => IslandPaths.CodexAuthFile;

    private static string StoreDir => Path.Combine(IslandPaths.CodexHome, "agentisland-accounts");

    public static IReadOnlyList<Account> Accounts()
    {
        try
        {
            if (!Directory.Exists(StoreDir)) return Array.Empty<Account>();
            return Directory.EnumerateFiles(StoreDir)
                .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .Select(path => new Account(
                    Path.GetFileNameWithoutExtension(path), path, EmailInFile(path)))
                .OrderBy(account => account.Label, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not list parked Codex accounts: {error.Message}");
            return Array.Empty<Account>();
        }
    }

    /// Label of the account currently live, matched by credential contents —
    /// null when the live file matches nothing parked (a fresh login).
    public static string? ActiveLabel()
    {
        if (ReadBytesOrNull(LivePath) is not { } live) return null;
        foreach (var parked in Accounts())
        {
            if (ReadBytesOrNull(parked.Path) is { } stored && stored.SequenceEqual(live))
            {
                return parked.Label;
            }
        }
        return null;
    }

    /// Park the CURRENT login under `label`, so it can be switched back to.
    public static bool ParkCurrent(string? label)
    {
        if (StoredPath(Sanitize(label)) is not { } target) return false;
        if (ReadBytesOrNull(LivePath) is not { } live) return false;
        try
        {
            Directory.CreateDirectory(StoreDir);
            Harden(StoreDir);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not create the Codex account store: {error.Message}");
            return false;
        }
        return WriteAtomic(target, live);
    }

    /// Make `account` the live Codex login. The outgoing file is parked first
    /// when it matches nothing on disk, so a hand-made login is never
    /// destroyed by a switch.
    public static bool Activate(Account account)
    {
        if (ReadBytesOrNull(account.Path) is not { } data) return false;
        if (ActiveLabel() is null)
        {
            ParkCurrent($"previous-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        }
        try
        {
            if (Path.GetDirectoryName(LivePath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not prepare the Codex home directory: {error.Message}");
            return false;
        }
        return WriteAtomic(LivePath, data);
    }

    public static bool Forget(Account account)
    {
        // Delete only inside our own store: the Account comes from a caller,
        // and this is the one code path that removes a credentials file.
        if (InStore(account.Path) is not { } target) return false;
        try
        {
            if (!File.Exists(target)) return false;
            File.Delete(target);
            return true;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not remove parked Codex account: {error.Message}");
            return false;
        }
    }

    // MARK: - Auto-switch (opt-in)

    /// Off by default: silently swapping logins surprises anyone who did not
    /// ask for it, and a mid-session file swap is visible to a running Codex
    /// CLI. The owner flips it on once and forgets it.
    public static bool AutoSwitchEnabled
    {
        get => Preferences.Get<bool?>(AutoSwitchKey) ?? false;
        set => Preferences.Set(AutoSwitchKey, value);
    }

    /// The next account worth rotating to: parked, not the live one, not yet
    /// tried this exhaustion episode. Stable label order, so rotation walks
    /// the pool deterministically instead of ping-ponging between two logins.
    public static Account? RotationCandidate(IReadOnlySet<string> tried)
    {
        var active = ActiveLabel();
        foreach (var account in Accounts())
        {
            if (string.Equals(account.Label, active, StringComparison.Ordinal)) continue;
            if (tried.Contains(account.Label)) continue;
            return account;
        }
        return null;
    }

    // MARK: - Helpers (unit-tested)

    /// Labels become filenames: keep them to safe characters so a stray "\" or
    /// ".." can never escape the store directory. Strips rather than rejects —
    /// a label is cosmetic, unlike a session id that would reach a shell — and
    /// every write re-checks the resolved path anyway.
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var kept = new StringBuilder(Math.Min(raw.Length, MaxLabelLength));
        foreach (var ch in raw)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != ' ') continue;
            kept.Append(ch);
            if (kept.Length >= MaxLabelLength) break;
        }
        // Trim AFTER the cap, not only before it: Win32 silently drops a
        // trailing space from a filename, so "work " and "work" would fight
        // over one file while reporting two different labels.
        var clean = kept.ToString().Trim();
        if (clean.Length == 0) return string.Empty;
        return ReservedNames.Contains(clean.ToUpperInvariant()) ? clean + "_" : clean;
    }

    /// Codex writes the account email in fields next to the token block; read
    /// the first email-shaped string. Never returns — and never logs — the
    /// token itself.
    internal static string? EmailInJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = property.Value.GetString();
                    if (text is not null && text.Contains('@') && !text.Contains(' ')) return text;
                    break;
                case JsonValueKind.Object:
                    if (EmailInJson(property.Value) is { } nested) return nested;
                    break;
            }
        }
        return null;
    }

    private static string? EmailInFile(string path)
    {
        try
        {
            if (ReadBytesOrNull(path) is not { } data) return null;
            using var document = JsonDocument.Parse(data);
            return EmailInJson(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    /// Where a sanitized label lives, or null when the label is empty.
    private static string? StoredPath(string cleanLabel) =>
        cleanLabel.Length == 0 ? null : InStore(Path.Combine(StoreDir, cleanLabel + ".json"));

    /// Resolve a path and confirm it sits DIRECTLY inside the store directory.
    /// Sanitize already makes an escape impossible for labels; this is the
    /// check that keeps the guarantee if Sanitize ever loosens, and the only
    /// gate a caller-supplied Account path passes through.
    private static string? InStore(string candidate)
    {
        try
        {
            var target = Path.GetFullPath(candidate);
            if (Path.GetDirectoryName(target) is not { } parent) return null;
            return string.Equals(
                parent.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(StoreDir).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
                ? target
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// A running Codex CLI holds auth.json open, and the default File.ReadAll*
    /// share mode throws against it. That failure is not cosmetic here:
    /// ActiveLabel() would report "matches nothing parked", and Activate()
    /// answers that by parking ANOTHER copy of the live login under a fresh
    /// previous-&lt;stamp&gt; name — one more file, and one more rotation
    /// candidate, every single switch.
    private static byte[]? ReadBytesOrNull(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// Temp file + rename, so a crash mid-write never leaves a truncated
    /// auth.json: the live file carries either the old login or the new one.
    /// The rename is retried because a running Codex CLI can hold a brief read
    /// lock on it.
    private static bool WriteAtomic(string path, byte[] data)
    {
        // Per-write temp name: Windows denies a second opener while the first
        // holds the handle, so two Agent Island instances auto-rotating at the
        // same exhaustion boundary would fail each other's write on a shared
        // ".tmp". The suffix keeps it out of Accounts(), which lists ".json".
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, data);
            Harden(tmp);
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
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not write Codex credentials: {error.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    /// Windows has no chmod 600. The store sits inside the user profile, so it
    /// inherits that profile's DACL — exactly the protection auth.json itself
    /// gets — and we additionally keep it out of the content index so a copy of
    /// a live token never lands in the Windows Search database.
    private static void Harden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.NotContentIndexed);
        }
        catch
        {
            // Best effort: a FAT volume or a policy-managed index setting is no
            // reason to refuse the switch.
        }
    }
}
