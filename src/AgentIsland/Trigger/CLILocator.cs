using System.IO;
using AgentIsland.Core;
using AgentIsland.Model;

namespace AgentIsland.Trigger;

/// Resolves the claude / codex / gemini / grok CLIs by probing known Windows
/// install locations before falling back to a PATH scan — the counterpart of
/// the macOS locator's Homebrew/nvm probes. All four ship as npm globals, so
/// the shim trio (name.cmd / name.ps1 / bare name) lands in whichever prefix
/// the user's Node manager owns, and none of those prefixes is guaranteed to
/// be on a GUI app's PATH. Claude Desktop's managed CLI lives under
/// %APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe and never is.
/// Cursor is app-only: it has no CLI to find.
public static class CLILocator
{
    /// Extension order is also preference order. `.ps1` comes last and is a
    /// presence signal more than a launch target — the alarm navigator spawns
    /// through `cmd /c`, which cannot run a bare PowerShell script, and npm
    /// always writes a `.cmd` next to the `.ps1` anyway.
    private static readonly string[] Extensions = { ".cmd", ".exe", ".bat", ".ps1" };

    public static string? PathFor(TriggerTool tool) => PathFor(tool.ToDisplayProvider());

    public static string? PathFor(DisplayProvider provider) =>
        ProviderIdentity.CliName(provider) is { } name ? Locate(name) : null;

    /// A miss walks ~90 candidate paths plus a PATH scan times four
    /// extensions, and settings-row refreshes ask several times per usage
    /// poll. 30 seconds is short enough that a CLI installed while the app is
    /// running still shows up almost immediately, and long enough that one
    /// poll's burst of refreshes costs a single probe.
    private static readonly TimeSpan MemoTtl = TimeSpan.FromSeconds(30);

    private static readonly object MemoGate = new();
    private static readonly Dictionary<string, (DateTimeOffset At, string? Path)> Memo =
        new(StringComparer.OrdinalIgnoreCase);

    public static string? Locate(string name)
    {
        var now = DateTimeOffset.UtcNow;
        lock (MemoGate)
        {
            if (Memo.TryGetValue(name, out var cached) && now - cached.At < MemoTtl)
            {
                // A cached HIT is still re-verified: an uninstall between
                // probes must not keep handing back a path to nothing.
                if (cached.Path is null || File.Exists(cached.Path)) return cached.Path;
            }
        }
        var located = Probe(name);
        lock (MemoGate)
        {
            Memo[name] = (now, located);
        }
        return located;
    }

    private static string? Probe(string name)
    {
        foreach (var candidate in Candidates(name).Where(File.Exists))
        {
            return candidate;
        }

        if (name == "claude" && ManagedClaudeCli() is { } managed) return managed;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in Extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var home = IslandPaths.Home;
        var roaming = IslandPaths.RoamingAppData;
        var local = IslandPaths.LocalAppData;
        var candidates = new List<string>
        {
            // npm's default global prefix on Windows.
            Path.Combine(roaming, "npm", name + ".cmd"),
            Path.Combine(roaming, "npm", name + ".exe"),
            Path.Combine(home, ".local", "bin", name + ".exe"),
            Path.Combine(home, ".local", "bin", name + ".cmd"),
            Path.Combine(home, ".bun", "bin", name + ".exe"),
            Path.Combine(home, ".bun", "bin", name + ".cmd"),
            Path.Combine(roaming, "npm", name + ".ps1"),
        };

        // `npm config set prefix` relocates the whole shim directory. npm
        // exports the choice as npm_config_prefix; users who set it by hand
        // usually spell it upper-case.
        foreach (var variable in new[] { "npm_config_prefix", "NPM_CONFIG_PREFIX" })
        {
            var prefix = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            AddWithExtensions(candidates, prefix, name);
        }

        // The other package managers each own a shim directory of their own.
        AddWithExtensions(candidates, Path.Combine(local, "pnpm"), name);
        AddWithExtensions(candidates, Path.Combine(local, "Volta", "bin"), name);
        AddWithExtensions(candidates, Path.Combine(local, "Yarn", "bin"), name);
        AddWithExtensions(candidates, Path.Combine(local, "Microsoft", "WinGet", "Links"), name);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            AddWithExtensions(candidates, Path.Combine(programFiles, "nodejs"), name);
        }

        // nvm-windows keeps one global shim set per Node version and only
        // symlinks the active one onto PATH; newest version wins, mirroring
        // the macOS locator's ~/.nvm scan.
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
        if (string.IsNullOrWhiteSpace(nvmHome)) nvmHome = Path.Combine(roaming, "nvm");
        foreach (var version in VersionDirectories(nvmHome))
        {
            AddWithExtensions(candidates, version, name);
        }

        // fnm installs each version under its own tree; global shims sit
        // beside the node binary in installation\.
        foreach (var version in VersionDirectories(Path.Combine(roaming, "fnm", "node-versions")))
        {
            AddWithExtensions(candidates, Path.Combine(version, "installation"), name);
        }

        return candidates;
    }

    private static void AddWithExtensions(List<string> candidates, string directory, string name)
    {
        foreach (var ext in Extensions)
        {
            candidates.Add(Path.Combine(directory, name + ext));
        }
    }

    /// Version-named subdirectories, newest first. A missing root is normal —
    /// most machines run exactly one Node manager.
    private static IReadOnlyList<string> VersionDirectories(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(dir => Path.GetFileName(dir), VersionAwareComparer.Instance)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// Claude Desktop vendors the CLI per version; pick the newest.
    private static string? ManagedClaudeCli()
    {
        var root = Path.Combine(IslandPaths.RoamingAppData, "Claude", "claude-code");
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(dir => Path.GetFileName(dir), VersionAwareComparer.Instance)
                .Select(dir => Path.Combine(dir, "claude.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    private sealed class VersionAwareComparer : IComparer<string?>
    {
        public static VersionAwareComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            // nvm-windows names its directories "v20.11.0"; Version.TryParse
            // rejects the prefix, which would sort v9 above v20.
            if (Version.TryParse(Trimmed(x), out var vx) && Version.TryParse(Trimmed(y), out var vy))
            {
                return vx.CompareTo(vy);
            }
            return string.CompareOrdinal(x, y);
        }

        private static string? Trimmed(string? raw) => raw?.TrimStart('v', 'V');
    }
}
