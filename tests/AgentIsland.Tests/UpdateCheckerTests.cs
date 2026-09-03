using AgentIsland.Update;

namespace AgentIsland.Tests;

/// Pins the update pipeline's pure logic. Version parsing: tags with and
/// without the "v" prefix parse, junk never reads as an update, ordering is
/// numeric (1.10.0 beats 1.9.9). Release parsing: the zip asset for this
/// machine's architecture is picked out of the mixed macOS/Windows release,
/// a release without one still reports its version, and junk JSON is
/// ignored. Swap planning: the .old/.new names sit next to the exe so both
/// renames stay on one volume.
public static class UpdateCheckerTests
{
    // Shape-faithful trim of a real unified release: DMG + appcast from the
    // macOS job, the Windows zip attached by the windows-release workflow.
    private const string UnifiedRelease = """
        {
          "tag_name": "v1.5.5",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "AgentIsland-1.5.5.dmg", "size": 4700000,
              "browser_download_url": "https://github.com/x/y/releases/download/v1.5.5/AgentIsland-1.5.5.dmg" },
            { "name": "appcast.xml", "size": 2048,
              "browser_download_url": "https://github.com/x/y/releases/download/v1.5.5/appcast.xml" },
            { "name": "AgentIsland-1.5.5-win-x64.zip", "size": 67858030,
              "browser_download_url": "https://github.com/x/y/releases/download/v1.5.5/AgentIsland-1.5.5-win-x64.zip" }
          ]
        }
        """;

    private const string MacOnlyRelease = """
        {
          "tag_name": "v1.6.0",
          "assets": [
            { "name": "AgentIsland-1.6.0.dmg", "size": 4700000,
              "browser_download_url": "https://github.com/x/y/releases/download/v1.6.0/AgentIsland-1.6.0.dmg" }
          ]
        }
        """;

    public static void RunAll()
    {
        Expect(UpdateChecker.ParseTag("v1.5.4") == new Version(1, 5, 4), "v-prefixed tag parses");
        Expect(UpdateChecker.ParseTag("1.5.4") == new Version(1, 5, 4), "bare tag parses");
        Expect(UpdateChecker.ParseTag("windows-test") is null, "junk tag is ignored");
        Expect(UpdateChecker.ParseTag("v1.10.0")! > new Version(1, 9, 9), "ordering is numeric, not lexical");

        var unified = UpdateChecker.ParseRelease(UnifiedRelease, "win-x64");
        Expect(unified is not null, "unified release parses");
        Expect(unified!.Version == new Version(1, 5, 5), "unified release version");
        Expect(unified.AssetName == "AgentIsland-1.5.5-win-x64.zip", "zip picked among dmg/appcast");
        Expect(unified.AssetUrl!.EndsWith("AgentIsland-1.5.5-win-x64.zip", StringComparison.Ordinal),
            "asset url is the zip's download url");
        Expect(unified.AssetSize == 67858030, "asset size carried for the download check");

        var wrongArch = UpdateChecker.ParseRelease(UnifiedRelease, "win-arm64");
        Expect(wrongArch is not null && wrongArch.AssetUrl is null,
            "x64 zip never offered to an arm64 machine");

        var macOnly = UpdateChecker.ParseRelease(MacOnlyRelease, "win-x64");
        Expect(macOnly is not null && macOnly.Version == new Version(1, 6, 0) && macOnly.AssetUrl is null,
            "mac-only release still reports its version, without an asset");

        Expect(UpdateChecker.ParseRelease("{not json", "win-x64") is null, "junk json is ignored");
        Expect(UpdateChecker.ParseRelease("""{"tag_name":"nightly","assets":[]}""", "win-x64") is null,
            "unparsable tag is ignored");

        var (oldPath, newPath) = UpdateInstaller.PlanSwap(@"C:\Apps\Agent Island\AgentIsland.exe");
        Expect(oldPath == @"C:\Apps\Agent Island\AgentIsland.exe.old", "swap .old sits next to the exe");
        Expect(newPath == @"C:\Apps\Agent Island\AgentIsland.exe.new", "swap .new sits next to the exe");

        Expect(UpdateChecker.RuntimeSuffix.StartsWith("win-", StringComparison.Ordinal),
            "runtime suffix matches build.ps1 zip naming");

        Console.WriteLine("UpdateCheckerTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
