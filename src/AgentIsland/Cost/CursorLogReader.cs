using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Cost;

/// Cursor's per-turn token ledger lives in the same state.vscdb the auth reader
/// opens: cursorDiskKV rows keyed `bubbleId:%`, each a JSON bubble carrying
/// tokenCount:{inputTokens, outputTokens} — no model, no cost.
///
/// Reading it needs real SQLite semantics, not a byte scan. Freed B-tree pages
/// and superseded WAL frames keep stale copies of bubbles (including ones from
/// chats the user deleted), so summing every tokenCount the raw bytes contain
/// would over-count — which the publish gate's honesty rule forbids outright.
/// The auth reader gets away with a regex because it lifts a single current
/// value; a SUM cannot.
///
/// The way out without taking a NuGet dependency: Windows 10 1607+ ships
/// `winsqlite3.dll` in System32 for its own components, and it exports the
/// standard C API. P/Invoking it gives the same live-row semantics macOS gets
/// from its system SQLite, with no package reference.
///
/// Every entry point is wrapped: a missing DLL, a renamed export, or any
/// marshalling fault degrades to "no events", exactly what this reader
/// returned before — it can never take the app down for a Cursor user.
public static class CursorLogReader
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadonly = 0x00000001;

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CharSet = CharSet.Ansi)]
    private static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close")]
    private static extern int Close(IntPtr db);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2")]
    private static extern int Prepare(IntPtr db, byte[] sql, int byteLength, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    private static extern int Step(IntPtr stmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr ColumnText(IntPtr stmt, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")]
    private static extern int ColumnBytes(IntPtr stmt, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    private static extern int FinalizeStatement(IntPtr stmt);

    private static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public static List<TokenEvent> Scan(int lookbackDays)
    {
        var output = new List<TokenEvent>();
        var path = DatabasePath;
        if (!File.Exists(path)) return output;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        DateTimeOffset fallback;
        try { fallback = new DateTimeOffset(File.GetLastWriteTimeUtc(path)); }
        catch (Exception) { fallback = DateTimeOffset.UtcNow; }

        var db = IntPtr.Zero;
        var statement = IntPtr.Zero;
        try
        {
            // Read-only and via the URI form so a running Cursor's lock never
            // blocks us; `immutable=1` also skips the -wal replay, matching
            // what the macOS reader does.
            var uri = "file:" + path.Replace('\\', '/') + "?mode=ro&immutable=1";
            if (Open(NullTerminated(uri), out db, SqliteOpenReadonly | 0x00000040, IntPtr.Zero) != SqliteOk)
            {
                return output;
            }

            const string sql = "SELECT value FROM cursorDiskKV WHERE key LIKE 'bubbleId:%'";
            if (Prepare(db, NullTerminated(sql), -1, out statement, IntPtr.Zero) != SqliteOk)
            {
                return output;
            }

            while (Step(statement) == SqliteRow)
            {
                var pointer = ColumnText(statement, 0);
                if (pointer == IntPtr.Zero) continue;
                var length = ColumnBytes(statement, 0);
                if (length <= 0) continue;
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                var json = System.Text.Encoding.UTF8.GetString(bytes);
                if (!json.Contains("tokenCount", StringComparison.Ordinal)) continue;

                var parsed = ParseBubble(json, fallback);
                if (parsed is null || parsed.Timestamp < cutoff) continue;
                output.Add(parsed);
            }
        }
        catch (Exception)
        {
            // DllNotFound, EntryPointNotFound, marshalling, corrupt db — all
            // mean the same thing here: no Cursor cost data this pass.
            return output;
        }
        finally
        {
            if (statement != IntPtr.Zero) { try { FinalizeStatement(statement); } catch (Exception) { } }
            if (db != IntPtr.Zero) { try { Close(db); } catch (Exception) { } }
        }

        return output;
    }

    private static byte[] NullTerminated(string value)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(value);
        var buffer = new byte[raw.Length + 1];
        Array.Copy(raw, buffer, raw.Length);
        return buffer;
    }

    /// Returns null for bubbles without a usable token count. Absent or {0,0}
    /// counts are skipped — the survey machine is all historical zero sessions,
    /// so the reader simply yields nothing there rather than a wall of zeros.
    internal static TokenEvent? ParseBubble(string json, DateTimeOffset fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tokenCount", out var counts)
                || counts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var input = ReadInt(counts, "inputTokens");
            var output = ReadInt(counts, "outputTokens");
            if (input == 0 && output == 0) return null;

            return new TokenEvent(
                TriggerTool.Cursor,
                BubbleDate(root, fallback),
                // A Cursor bubble carries no model field; a constant keeps
                // downstream grouping stable. Nothing prices it, so cost is null.
                "cursor",
                input,
                output,
                0,
                0,
                null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    /// A bubble's `createdAt` when present (epoch ms as Cursor writes it, or an
    /// ISO string defensively), else the db mtime. The shape is unproven on the
    /// survey machine, so parsing is best-effort with a real-clock fallback.
    private static DateTimeOffset BubbleDate(JsonElement root, DateTimeOffset fallback)
    {
        if (!root.TryGetProperty("createdAt", out var raw)) return fallback;
        if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out var epoch))
        {
            // Values this large are milliseconds; anything smaller is seconds.
            return epoch > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        if (raw.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(raw.GetString(), out var parsed))
        {
            return parsed;
        }
        return fallback;
    }
}
