using System.IO;
namespace AgentIsland.Usage;

/// Where Antigravity keeps itself on disk. Google has renamed the data
/// directory twice already (1.x `antigravity`, 2.x `antigravity-ide`, plus
/// the separate `antigravity-cli` root), so every known variant is probed
/// rather than one hardcoded guess.
///
/// Sign-in state is deliberately NOT probed here: the CLI parks its token
/// in the platform credential store (go-keyring), and the quota fetch
/// against the local language server is the honest liveness signal anyway —
/// a stored token proves nothing about the server being reachable.
public static class AntigravityCredentials
{
    public static string GeminiHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");

    private static readonly string[] RootNames = { "antigravity", "antigravity-ide", "antigravity-cli" };

    /// Existing data roots, CLI first. Empty means Antigravity has never
    /// run on this machine.
    public static List<string> DataRoots()
    {
        var roots = new List<string>();
        foreach (var name in RootNames)
        {
            var path = Path.Combine(GeminiHome, name);
            if (Directory.Exists(path)) roots.Add(path);
        }
        return roots;
    }

    public static bool Detected => DataRoots().Count > 0;
}
