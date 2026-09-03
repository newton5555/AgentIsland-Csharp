using System.Runtime.InteropServices;
using AgentIsland.Core;

namespace AgentIsland.Alarm;

/// Answers one question for the turn alarm: is the app hosting this
/// session's CLI the one the user is looking at RIGHT NOW? (If so, an alarm
/// window on top of it is noise — they can see the finished turn.)
///
/// Port of the macOS AgentHostAppResolver, in Win32 vocabulary: find CLI
/// processes (claude/codex, or the node/bun runtimes that host them) whose
/// working directory matches the session's cwd, walk each one's parent
/// chain (Terminal, VS Code, a Claude desktop window…), and check whether
/// the foreground window's process sits on that chain. Anything that can't
/// be resolved — empty cwd, a daemonized CLI whose parent died, access
/// denied — answers false, so the alarm FAILS OPEN and pops as it always
/// did.
public static class AgentHostAppResolver
{
    /// Executable basenames that can be the CLI process itself. The npm
    /// shims run the CLIs under node (or bun); matching by cwd does the real
    /// discrimination, these names just keep the PEB reads cheap.
    private static readonly string[] CliNames = { "claude", "codex", "agy", "antigravity", "grok", "node", "bun" };

    /// Provider-aware entry point. Cursor is the editor AND the agent host,
    /// so a finished turn is already visible in the pane the user is looking
    /// at — an alarm on top of it is pure noise. It has no CLI and its
    /// sessions carry no cwd, so the process-chain walk below can never
    /// catch it; the foreground executable name is the whole test.
    public static bool IsHostAppFrontmost(TriggerTool provider, string? cwd)
    {
        if (provider == TriggerTool.Cursor) return IsForegroundExe("cursor");
        if (provider == TriggerTool.Antigravity && IsForegroundExe("antigravity")) return true;
        if (provider == TriggerTool.Grok && IsForegroundExe("grok")) return true;
        return IsHostAppFrontmost(cwd);
    }

    private static bool IsForegroundExe(string name)
    {
        try
        {
            var foreground = ForegroundProcessId();
            if (foreground == 0) return false;
            foreach (var entry in SnapshotProcesses())
            {
                if (entry.Pid != foreground) continue;
                var exe = entry.ExeName;
                if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    exe = exe[..^4];
                }
                return string.Equals(exe, name, StringComparison.OrdinalIgnoreCase)
                    || exe.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch
        {
            // fail open
        }
        return false;
    }

    public static bool IsHostAppFrontmost(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        try
        {
            var foreground = ForegroundProcessId();
            if (foreground == 0) return false;

            var processes = SnapshotProcesses();
            if (processes.Count == 0) return false;
            var byPid = new Dictionary<uint, ProcessEntry>();
            foreach (var entry in processes)
            {
                byPid[entry.Pid] = entry;
            }

            var wanted = NormalizePath(cwd);
            foreach (var entry in processes)
            {
                if (!IsCliName(entry.ExeName)) continue;
                var processCwd = TryReadProcessCwd(entry.Pid);
                if (processCwd is null || NormalizePath(processCwd) != wanted) continue;
                if (ChainReachesForeground(entry, byPid, foreground)) return true;
            }
        }
        catch
        {
            // fail open
        }
        return false;
    }

    private static bool IsCliName(string exeName)
    {
        var name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName[..^4]
            : exeName;
        foreach (var cli in CliNames)
        {
            if (string.Equals(name, cli, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// Windows recycles PIDs aggressively, so a bare ppid walk can wander
    /// into an unrelated process. A parent that started AFTER its child
    /// can't be the real parent — drop the link there (macOS skips this
    /// check; its pid space wraps far slower).
    private static bool ChainReachesForeground(
        ProcessEntry start, Dictionary<uint, ProcessEntry> byPid, uint foreground)
    {
        if (start.Pid == foreground) return true;
        var current = start;
        for (var hops = 0; hops < 24; hops++)
        {
            var parentPid = current.ParentPid;
            if (parentPid <= 4 || parentPid == current.Pid) return false;
            if (!byPid.TryGetValue(parentPid, out var parent)) return false;
            if (parent.CreateTime > current.CreateTime) return false;
            if (parent.Pid == foreground) return true;
            current = parent;
        }
        return false;
    }

    // MARK: - Foreground

    private static uint ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        _ = GetWindowThreadProcessId(window, out var pid);
        return pid;
    }

    // MARK: - Process snapshot (Toolhelp32)

    private readonly record struct ProcessEntry(uint Pid, uint ParentPid, string ExeName, long CreateTime);

    private static List<ProcessEntry> SnapshotProcesses()
    {
        var result = new List<ProcessEntry>(256);
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return result;
            do
            {
                result.Add(new ProcessEntry(
                    entry.th32ProcessID,
                    entry.th32ParentProcessID,
                    entry.szExeFile,
                    CreateTimeOf(entry.th32ProcessID)));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private static long CreateTimeOf(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            return GetProcessTimes(handle, out var create, out _, out _, out _) ? create : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // MARK: - Working directory (PEB read)

    /// The current directory lives in the target's PEB → ProcessParameters →
    /// CurrentDirectory.DosPath — three remote reads. 64-bit offsets only:
    /// the CLIs (node, bun, the shims) are 64-bit on every machine this app
    /// supports, and a failed read just means "not resolvable" (fail open).
    private static string? TryReadProcessCwd(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
            {
                return null;
            }
            if (info.PebBaseAddress == IntPtr.Zero) return null;

            // PEB+0x20 → RTL_USER_PROCESS_PARAMETERS*
            if (!ReadPointer(handle, info.PebBaseAddress + 0x20, out var parameters) || parameters == IntPtr.Zero)
            {
                return null;
            }
            // +0x38 → CurrentDirectory.DosPath (UNICODE_STRING { ushort Length, ushort Max, pad, char* Buffer })
            var dosPath = parameters + 0x38;
            var header = new byte[16];
            if (!ReadBytes(handle, dosPath, header)) return null;
            var length = BitConverter.ToUInt16(header, 0);
            var buffer = (IntPtr)BitConverter.ToInt64(header, 8);
            if (length == 0 || length > 4096 || buffer == IntPtr.Zero) return null;

            var chars = new byte[length];
            if (!ReadBytes(handle, buffer, chars)) return null;
            return System.Text.Encoding.Unicode.GetString(chars);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool ReadPointer(IntPtr process, IntPtr address, out IntPtr value)
    {
        var buffer = new byte[8];
        if (!ReadBytes(process, address, buffer))
        {
            value = IntPtr.Zero;
            return false;
        }
        value = (IntPtr)BitConverter.ToInt64(buffer, 0);
        return true;
    }

    private static bool ReadBytes(IntPtr process, IntPtr address, byte[] buffer) =>
        ReadProcessMemory(process, address, buffer, buffer.Length, out var read)
        && read == buffer.Length;

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal)) trimmed = trimmed[4..];
        trimmed = trimmed.TrimEnd('\\', '/');
        return trimmed.ToUpperInvariant();
    }

    // MARK: - P/Invoke

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(
        IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int informationClass, ref PROCESS_BASIC_INFORMATION information,
        int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(
        IntPtr process, IntPtr address, [Out] byte[] buffer, int size, out int read);
}
