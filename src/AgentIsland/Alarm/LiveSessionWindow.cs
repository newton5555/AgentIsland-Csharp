using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentIsland.Alarm;

/// "会话活在哪就跳哪" (macOS 2.1.2): when the CLI that finished a turn is
/// still RUNNING, the alarm click should land in its live terminal window,
/// not spawn a fresh one over it. Windows terminals put the window on a
/// different process than the CLI (Windows Terminal is an ancestor, classic
/// conhost a child), so the walk goes both ways from the CLI pid.
///
/// Deliberately conservative: it only fires when exactly ONE process of the
/// CLI's name is running — with two live claude sessions there is no way to
/// know which one finished without reading foreign-process PEBs, and
/// focusing the wrong session is worse than a fresh terminal.
public static class LiveSessionWindow
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SwRestore = 9;

    /// Try to front the live window hosting the given CLI. True when a
    /// window was found and raised — the caller skips its terminal spawn.
    public static bool TryFocus(params string[] processNames)
    {
        try
        {
            var matches = new List<Process>();
            foreach (var process in Process.GetProcesses())
            {
                if (processNames.Any(name =>
                        process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            try
            {
                if (matches.Count != 1) return false;
                return TryFocusHostWindow(matches[0].Id);
            }
            finally
            {
                foreach (var process in matches) process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFocusHostWindow(int cliPid)
    {
        var parents = ParentMap();
        // Ancestors first (Windows Terminal, wezterm, alacritty hosting the
        // shell that ran the CLI), then direct children (classic conhost,
        // which the CLI spawns itself).
        var candidates = new List<int>();
        var pid = cliPid;
        for (var hop = 0; hop < 8 && pid > 4; hop++)
        {
            candidates.Add(pid);
            if (!parents.TryGetValue(pid, out var parent) || parent == pid) break;
            pid = parent;
        }
        foreach (var (child, parent) in parents)
        {
            if (parent == cliPid) candidates.Add(child);
        }

        foreach (var candidate in candidates)
        {
            if (MainWindow(candidate) is { } window && window != IntPtr.Zero)
            {
                if (IsIconic(window)) ShowWindow(window, SwRestore);
                return SetForegroundWindow(window);
            }
        }
        return false;
    }

    private static IntPtr? MainWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var handle = process.MainWindowHandle;
            return handle == IntPtr.Zero ? null : handle;
        }
        catch
        {
            return null;
        }
    }

    // MARK: - Parent map (Toolhelp snapshot)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint Th32csSnapProcess = 0x2;

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }
}
