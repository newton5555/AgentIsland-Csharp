using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentIsland.Core;

/// Reads Cursor's conversation store — the same globalStorage/state.vscdb the
/// cost reader opens, through the SQLite that Windows itself ships
/// (winsqlite3.dll, present since Windows 10 1607). No NuGet dependency, and
/// real B-tree semantics rather than a byte scan that would surface rows from
/// freed pages.
///
/// Every entry point is wrapped: a missing DLL, a renamed export, or a
/// marshalling fault degrades to "no conversations", which reads as a Cursor
/// user with nothing running. It can never take the app down.
internal static class CursorConversations
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadonly = 0x00000001;
    private const int SqliteOpenUri = 0x00000040;

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2")]
    private static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close")]
    private static extern int Close(IntPtr db);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2")]
    private static extern int Prepare(IntPtr db, byte[] sql, int byteLength, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text")]
    private static extern int BindText(IntPtr stmt, int index, byte[] value, int byteLength, IntPtr destructor);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    private static extern int Step(IntPtr stmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr ColumnText(IntPtr stmt, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")]
    private static extern int ColumnBytes(IntPtr stmt, int column);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    private static extern int FinalizeStatement(IntPtr stmt);

    /// The newest message of every conversation, as (composerId, bubble JSON).
    /// rowid order is insertion order, so the highest rowid for a composer is
    /// its latest bubble — far cheaper than decoding every bubble to sort.
    internal static List<(string ComposerId, string Json)> NewestBubblePerConversation(string dbPath)
    {
        var output = new List<(string, string)>();
        var db = IntPtr.Zero;
        try
        {
            // URI form with immutable=1: a running Cursor holds the write
            // lock, and skipping the -wal replay keeps this read cheap.
            var uri = "file:" + dbPath.Replace('\\', '/') + "?mode=ro&immutable=1";
            if (Open(NullTerminated(uri), out db, SqliteOpenReadonly | SqliteOpenUri, IntPtr.Zero) != SqliteOk)
            {
                return output;
            }

            foreach (var composerId in ComposerIds(db))
            {
                var json = NewestBubble(db, composerId);
                if (json is not null) output.Add((composerId, json));
            }
        }
        catch (Exception)
        {
            return output;
        }
        finally
        {
            if (db != IntPtr.Zero) { try { Close(db); } catch (Exception) { } }
        }
        return output;
    }

    /// A bubble's own first line stands in for a conversation title — Cursor
    /// leaves composerData.name empty on most threads.
    internal static string? Title(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var raw = text.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var line = raw.Split('\n', 2)[0].Trim();
            if (line.Length == 0) return null;
            return line.Length > 48 ? line[..48] : line;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> ComposerIds(IntPtr db)
    {
        var ids = new List<string>();
        var statement = IntPtr.Zero;
        try
        {
            const string sql = "SELECT key FROM cursorDiskKV WHERE key LIKE 'composerData:%'";
            if (Prepare(db, NullTerminated(sql), -1, out statement, IntPtr.Zero) != SqliteOk) return ids;
            while (Step(statement) == SqliteRow)
            {
                var key = ReadColumnText(statement);
                if (key is null) continue;
                var id = key["composerData:".Length..];
                if (id.Length > 0) ids.Add(id);
            }
        }
        catch (Exception)
        {
            // fall through with whatever we collected
        }
        finally
        {
            if (statement != IntPtr.Zero) { try { FinalizeStatement(statement); } catch (Exception) { } }
        }
        return ids;
    }

    private static string? NewestBubble(IntPtr db, string composerId)
    {
        var statement = IntPtr.Zero;
        try
        {
            const string sql = "SELECT value FROM cursorDiskKV WHERE key LIKE ? ORDER BY rowid DESC LIMIT 1";
            if (Prepare(db, NullTerminated(sql), -1, out statement, IntPtr.Zero) != SqliteOk) return null;
            // -1 destructor = SQLITE_TRANSIENT: SQLite copies the pattern, so
            // the managed buffer may be collected right after the call.
            BindText(statement, 1, NullTerminated("bubbleId:" + composerId + ":%"), -1, new IntPtr(-1));
            return Step(statement) == SqliteRow ? ReadColumnText(statement) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (statement != IntPtr.Zero) { try { FinalizeStatement(statement); } catch (Exception) { } }
        }
    }

    private static string? ReadColumnText(IntPtr statement)
    {
        var pointer = ColumnText(statement, 0);
        if (pointer == IntPtr.Zero) return null;
        var length = ColumnBytes(statement, 0);
        if (length <= 0) return null;
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static byte[] NullTerminated(string value)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(value);
        var buffer = new byte[raw.Length + 1];
        Array.Copy(raw, buffer, raw.Length);
        return buffer;
    }
}
