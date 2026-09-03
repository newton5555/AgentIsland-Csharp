using System.Runtime.InteropServices;
using AgentIsland.Core;
using AgentIsland.Core.Cost;

namespace AgentIsland.Windows.Storage;

/// Reads Cursor's token rows through the SQLite runtime shipped by Windows.
/// The parser is supplied by the caller so this project owns only the native
/// database boundary; provider-specific JSON interpretation stays reusable.
public static class CursorDatabaseReader
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteOpenReadonly = 0x00000001;
    private const int SqliteOpenUri = 0x00000040;

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

    public static List<TokenEvent> Scan(
        int lookbackDays,
        Func<string, DateTimeOffset, TokenEvent?> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);

        var output = new List<TokenEvent>();
        var path = IslandPaths.CursorGlobalStorageDatabase;
        if (!File.Exists(path)) return output;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        var fallback = SafeFileSystem.LastWriteTime(path);
        if (fallback == DateTimeOffset.MinValue) fallback = DateTimeOffset.UtcNow;

        var db = IntPtr.Zero;
        var statement = IntPtr.Zero;
        try
        {
            // Read-only immutable mode never waits behind Cursor's writer and
            // avoids treating old WAL frames as current rows.
            var uri = "file:" + path.Replace('\\', '/') + "?mode=ro&immutable=1";
            if (Open(NullTerminated(uri), out db, SqliteOpenReadonly | SqliteOpenUri, IntPtr.Zero)
                != SqliteOk)
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
                var json = ReadColumnText(statement);
                if (json is null || !json.Contains("tokenCount", StringComparison.Ordinal)) continue;
                var tokenEvent = parse(json, fallback);
                if (tokenEvent is not { } parsed || parsed.Timestamp < cutoff) continue;
                output.Add(parsed);
            }
        }
        catch
        {
            // A missing DLL, corrupt store, or concurrent replacement is a
            // partial scan, not an application-level failure.
            return output;
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                try { FinalizeStatement(statement); } catch { }
            }
            if (db != IntPtr.Zero)
            {
                try { Close(db); } catch { }
            }
        }

        return output;
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
