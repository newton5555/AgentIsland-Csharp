using System.IO;

namespace AgentIsland.Providers.Cost.Codex;

public readonly record struct CodexRolloutFile(
    string FullPath,
    string HomeKey,
    string RelativeName,
    bool Archived);

/// Sessions copy wins when the same relative path exists in archived_sessions.
public static class CodexRolloutDiscovery
{
    public const string ParserVersion = "codex-replay-v3";

    public static IReadOnlyList<CodexRolloutFile> PreferActive(IEnumerable<CodexRolloutFile> files)
    {
        var chosen = new Dictionary<(string Home, string Relative), CodexRolloutFile>(
            new HomeRelativeComparer());
        foreach (var file in files)
        {
            var key = (file.HomeKey, file.RelativeName);
            if (!chosen.TryGetValue(key, out var existing))
            {
                chosen[key] = file;
                continue;
            }

            if (existing.Archived && !file.Archived)
                chosen[key] = file;
        }

        return chosen.Values.ToList();
    }

    public static IReadOnlyList<CodexRolloutFile> FromHomes(IEnumerable<string> homes)
    {
        var found = new List<CodexRolloutFile>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home)) continue;
            var fullHome = Path.GetFullPath(home);
            AddTree(found, fullHome, Path.Combine(fullHome, "sessions"), archived: false);
            AddTree(found, fullHome, Path.Combine(fullHome, "archived_sessions"), archived: true);
        }

        return PreferActive(found);
    }

    private static void AddTree(
        List<CodexRolloutFile> output,
        string home,
        string root,
        bool archived)
    {
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(root)) return;
            files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var path in files)
        {
            string relative;
            try
            {
                relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            }
            catch
            {
                relative = Path.GetFileName(path);
            }

            output.Add(new CodexRolloutFile(path, home, relative, archived));
        }
    }

    private sealed class HomeRelativeComparer : IEqualityComparer<(string Home, string Relative)>
    {
        public bool Equals((string Home, string Relative) x, (string Home, string Relative) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Home, y.Home)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Relative, y.Relative);

        public int GetHashCode((string Home, string Relative) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Home),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Relative));
    }
}
