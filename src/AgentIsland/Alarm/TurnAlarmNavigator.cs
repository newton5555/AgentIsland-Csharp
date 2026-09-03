using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Alarm;

/// "Open thread" routing, mirroring the macOS navigator: activate the desktop
/// app through its registered URI and foreground an existing window when one
/// is available. CLI sessions reopen the interactive resume command in a terminal.
public static class TurnAlarmNavigator
{
    /// Resumes the session in a terminal via the provider's CLI — the same
    /// mechanism the auto-trigger engine uses and knows works. There is NO
    /// deep link that resumes a session by id (`claude://resume?sessionId=…`
    /// is fictional — the registered schemes only ever open NEW sessions), so
    /// the old .claudeDesktop branch merely surfaced Claude on its default
    /// view and never landed on the thread. All of it runs off the UI thread
    /// (CLILocator probes every PATH dir — seconds on a dead share); spawning
    /// a terminal doesn't need us to hold the foreground.
    /// Returns true only when a terminal (or, as a last resort, the desktop
    /// app) genuinely launched. Runs off the UI thread — CLILocator probes
    /// every PATH dir (seconds on a dead share) — so the caller awaits and
    /// keeps the alarm up until the result, surfacing an error on failure
    /// instead of vanishing with nothing opened.
    public static System.Threading.Tasks.Task<bool> Open(TriggerTool provider, ActivityMonitor.ActiveThread thread)
    {
        var sessionId = Sanitize(thread.SessionId);
        if (sessionId.Length == 0) return System.Threading.Tasks.Task.FromResult(false);
        var cwd = thread.Cwd;

        if (provider == TriggerTool.Grok)
        {
            // Verified against grok --help (2026-08-08): no per-id resume,
            // only -c/--continue = the most recent session for the current
            // working directory — which, launched from the thread's own cwd,
            // is the thread that just finished.
            return System.Threading.Tasks.Task.Run(() =>
                LiveSessionWindow.TryFocus("grok")
                || (Trigger.CLILocator.Locate("grok") is { } grok
                    && RunResumeInTerminal(grok, "--continue", cwd, "Grok continue")));
        }
        if (provider == TriggerTool.Antigravity)
        {
            // If Antigravity IDE (desktop app) is running, bring its window to the
            // foreground first — the user lives in the IDE, not a spawned terminal.
            // When running in a terminal, focus the live terminal or resume via CLI.
            return System.Threading.Tasks.Task.Run(() =>
                FocusAppWindow("Antigravity", "antigravity", "antigravity-ide")
                || LiveSessionWindow.TryFocus("agy", "antigravity")
                || (Trigger.CLILocator.Locate("agy") is { } agy
                    && RunResumeInTerminal(
                        agy, $"--conversation {sessionId}", cwd, "Antigravity resume")));
        }
        if (provider == TriggerTool.Cursor)
        {
            // No CLI, no per-session route — fronting the editor is the whole
            // gesture. Falls through gracefully when Cursor is not running.
            return System.Threading.Tasks.Task.Run(() => FocusAppWindow("Cursor"));
        }

        if (provider == TriggerTool.Claude)
        {
            // Desktop sessions: bring Claude Desktop forward, nothing more —
            // there is no deep link that lands on an existing conversation,
            // and a Desktop user clicking "Open thread" expects their Desktop
            // window, not a terminal popping up (mirrors the macOS split).
            // CLI sessions: resume for real via `claude --resume` from the
            // session's own cwd; that user lives in a terminal already.
            if (thread.LaunchTarget == SessionLaunchTarget.ClaudeDesktop)
            {
                // The "open via CLI instead" preference is retired (macOS
                // 1.6.1): desktop sessions always land back in the desktop
                // app. Clipboard assist (STA/UI thread required): until
                // Anthropic unlocks the conversation deep link, finding the
                // thread is one paste in Claude's search.
                if (!string.IsNullOrEmpty(thread.Label))
                {
                    try { System.Windows.Clipboard.SetText(thread.Label); } catch { }
                }
                return System.Threading.Tasks.Task.Run(() =>
                {
                    // claude://code/<bridge-id> routes straight to the
                    // conversation once Anthropic's server-side flag opens
                    // (today it merely fronts the app — same as our fallback).
                    if (BridgeSessionId(thread.SessionId) is { } bridge)
                    {
                        if (TryOpenUri(ClaudeDesktopUri(bridge)))
                        {
                            FocusAppWindow("claude"); // best effort; activation is asynchronous
                            return true;
                        }
                    }
                    // Closing Claude hides its last window but leaves the app
                    // running. With no HWND to focus, protocol activation asks
                    // Windows to restore the official Desktop app.
                    return FocusAppWindow("claude") || TryOpenUri(ClaudeDesktopUri(null));
                });
            }
            return System.Threading.Tasks.Task.Run(() =>
            {
                // A single live claude process = the session is still open
                // in its terminal; front it instead of spawning a twin.
                if (LiveSessionWindow.TryFocus("claude")) return true;
                if (Trigger.CLILocator.Locate("claude") is { } claude)
                {
                    return RunResumeInTerminal(claude, $"--resume {sessionId}", cwd, "Claude resume");
                }
                // No CLI on PATH — best effort: bring Claude Desktop forward
                // (it can't resume by id, but it's better than nothing).
                return FocusAppWindow("claude");
            });
        }

        // Codex sessions: the desktop deep link lands on the exact thread in
        // the app the user is looking at, so it goes FIRST (mirrors macOS;
        // the CLI-first preference is retired). A user chatting in the Codex
        // desktop app must not get a terminal popped at them just because
        // the CLI happens to be installed. The CLI resume is the fallback
        // when no codex:// handler exists.
        return System.Threading.Tasks.Task.Run(() =>
        {
            // Same live-window-first rule as macOS: an open TUI session
            // beats both the desktop deep link and a fresh terminal.
            if (LiveSessionWindow.TryFocus("codex")) return true;
            if (TryOpenUri($"codex://threads/{sessionId}"))
            {
                FocusAppWindow("Codex");   // best effort; the URI already landed
                return true;
            }
            if (Trigger.CLILocator.Locate("codex") is { } codex)
            {
                return RunResumeInTerminal(codex, $"resume {sessionId}", cwd, "Codex resume");
            }
            return false;
        });
    }

    /// Claude Desktop's session store keeps, per conversation, the internal
    /// bridge ids (session_/cse_ prefixed) its claude://code/<id> route
    /// expects. Looked up on click; the store is dozens of small JSON files.
    private static string? BridgeSessionId(string cliSessionId)
    {
        try
        {
            var root = IslandPaths.ClaudeDesktopSessionsRoot;
            if (!System.IO.Directory.Exists(root)) return null;
            foreach (var path in System.IO.Directory.EnumerateFiles(root, "local_*.json", System.IO.SearchOption.AllDirectories))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
                var rootEl = doc.RootElement;
                if (Jsonl.GetString(rootEl, "cliSessionId") != cliSessionId) continue;
                if (!rootEl.TryGetProperty("bridgeSessionIds", out var bridges)
                    || bridges.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return null;
                }
                string? found = null;
                foreach (var b in bridges.EnumerateArray())
                {
                    var v = b.GetString();
                    if (v is not null && (v.StartsWith("session_") || v.StartsWith("cse_"))) found = v;
                }
                return found;
            }
        }
        catch
        {
        }
        return null;
    }

    private static bool TryOpenUri(string uri)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch
        {
            // No handler registered for the scheme — fall through to the CLI.
            return false;
        }
    }

    internal static string ClaudeDesktopUri(string? bridgeSessionId) =>
        bridgeSessionId is null ? "claude://" : $"claude://code/{bridgeSessionId}";

    /// Brings the named app's main window up: restores it when minimized and
    /// takes the foreground. Uses EnumWindows to reliably locate Electron/Chromium
    /// top-level windows that do not set Process.MainWindowHandle.
    private static bool FocusAppWindow(params string[] processKeywords)
    {
        try
        {
            var pids = new HashSet<uint>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var pName = process.ProcessName;
                    foreach (var kw in processKeywords)
                    {
                        if (pName.Equals(kw, StringComparison.OrdinalIgnoreCase)
                            || pName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            pids.Add((uint)process.Id);
                            break;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (pids.Count == 0) return false;

            var matchingHwnds = new List<IntPtr>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pids.Contains(pid) && GetWindowTextLength(hwnd) > 0)
                {
                    matchingHwnds.Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);

            foreach (var hwnd in matchingHwnds)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                BringWindowToTop(hwnd);
                SwitchToThisWindow(hwnd, true);
                SetForegroundWindow(hwnd);
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    /// Opens a visible terminal that resumes the session. Uses the cmd.exe
    /// `start` launcher (reliable, known quoting) rather than Windows Terminal
    /// — wt's own tokenizer mangles the quoted binary path for paths with
    /// spaces and, worse, "succeeds" while showing a broken tab, so it can't
    /// be trusted for the one thing that must work. Returns whether the
    /// terminal actually launched.
    private static bool RunResumeInTerminal(string binary, string arguments, string cwd, string title)
    {
        var directory = ValidDirectory(cwd);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"{title}\" /D \"{directory}\" cmd /k \"\"{binary}\" {arguments}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"AgentIsland: resume launch failed: {error.Message}");
            return false;
        }
    }

    private static string ValidDirectory(string cwd) =>
        !string.IsNullOrEmpty(cwd) && System.IO.Directory.Exists(cwd) ? cwd : IslandPaths.Home;

    /// Session ids feed deep links and shell commands. Reject anything
    /// outside the safe alphabet outright — stripping characters would
    /// silently resume a DIFFERENT session id.
    internal static string Sanitize(string sessionId) =>
        Regex.IsMatch(sessionId, "^[A-Za-z0-9_-]+$") ? sessionId : "";
}
