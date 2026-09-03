using System.IO;
using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Pins CwdFromClaudeTranscript — the fix for the "resume runs from the
/// wrong directory" bug. A hyphenated project path (agent-island) must come
/// from the transcript's authoritative "cwd" field, never the lossy
/// un-munged folder name.
public static class ScannerCwdTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("cwd read from line 1", TestCwdFromFirstLine),
            ("cwd found past a non-json first line", TestCwdSkipsGarbageLine),
            ("hyphenated path preserved exactly", TestHyphenatedPathPreserved),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("ScannerCwdTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string WriteTranscript(params string[] lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "session.jsonl");
        File.WriteAllLines(file, lines);
        return file;
    }

    private static string JsonCwd(string cwd) =>
        "{\"type\":\"user\",\"cwd\":\"" + cwd.Replace("\\", "\\\\") + "\",\"message\":{}}";

    private static void TestCwdFromFirstLine()
    {
        const string cwd = @"C:\Users\me\projects\demo";
        var file = WriteTranscript(JsonCwd(cwd), "{\"type\":\"assistant\"}");
        try
        {
            Expect(SessionScanner.CwdFromClaudeTranscript(file) == cwd,
                "cwd must come from the transcript's cwd field");
        }
        finally { CleanUp(file); }
    }

    private static void TestCwdSkipsGarbageLine()
    {
        const string cwd = @"C:\dev\thing";
        var file = WriteTranscript("not json at all", JsonCwd(cwd));
        try
        {
            Expect(SessionScanner.CwdFromClaudeTranscript(file) == cwd,
                "a non-JSON first line must not stop the cwd search");
        }
        finally { CleanUp(file); }
    }

    private static void TestHyphenatedPathPreserved()
    {
        // The whole point: un-munging the folder name would turn this into
        // //home/user/agent/island; the cwd field keeps the real hyphens.
        const string cwd = @"C:\Users\me\agent-island";
        var file = WriteTranscript(JsonCwd(cwd));
        try
        {
            Expect(SessionScanner.CwdFromClaudeTranscript(file) == cwd,
                "hyphens in the real path must be preserved verbatim");
        }
        finally { CleanUp(file); }
    }

    private static void CleanUp(string file)
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
        catch { }
    }
}
