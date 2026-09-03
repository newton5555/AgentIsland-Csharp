using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Console test runner in the style of the macOS repo's script-level suites:
/// hand-rolled assertions, PASS per case, GREEN per suite, exit 1 on failure.
/// `AgentIsland.Tests.exe scan` instead runs a live MonitoringScan against
/// this machine's real session files — a diagnostic, not a test.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "scan")
        {
            return LiveScan();
        }
        if (args.Length > 0 && args[0] == "locate")
        {
            foreach (var name in new[] { "claude", "codex", "wt" })
            {
                Console.WriteLine($"{name} -> {Trigger.CLILocator.Locate(name) ?? "(null)"}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "scanbench")
        {
            // Time repeated full MonitoringScans against this machine's real
            // session files: scan #0 builds the turn-parse cache, #1+ hit it,
            // so the delta isolates the tail-read/parse cost from the fixed
            // file-enumeration + stat cost that every scan pays.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var now = DateTimeOffset.UtcNow;
            var lw = new Dictionary<string, DateTimeOffset>();
            for (var i = 0; i < 6; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var s = SessionScanner.MonitoringScan(now, lw);
                sw.Stop();
                Console.WriteLine($"scan #{i}: {sw.ElapsedMilliseconds} ms, {s.Count} sessions");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "weblogin-url")
        {
            // Live diagnostic: the EXACT authorize URL the in-app re-auth
            // opens, with a throwaway PKCE pair — paste into a logged-in
            // browser to see what Anthropic's consent page makes of it.
            var verifier = Usage.ClaudeWebLogin.RandomUrlSafe(32);
            var state = Usage.ClaudeWebLogin.RandomUrlSafe(16);
            var challenge = Usage.ClaudeWebLogin.Base64Url(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.ASCII.GetBytes(verifier)));
            Console.WriteLine(Usage.ClaudeWebLogin.BuildAuthorizeUrl(
                challenge, state, "http://localhost:54545/callback"));
            return 0;
        }
        if (args.Length > 0 && args[0] == "update-check")
        {
            // Live diagnostic: what would the updater see right now?
            // Honors AGENTISLAND_UPDATE_FEED the same way the app does.
            var info = Update.UpdateChecker.FetchLatestAsync().GetAwaiter().GetResult();
            Console.WriteLine($"current  {Update.UpdateChecker.CurrentVersion}");
            Console.WriteLine($"runtime  {Update.UpdateChecker.RuntimeSuffix}");
            if (info is null)
            {
                Console.WriteLine("feed     unreachable (or unparsable)");
                return 1;
            }
            Console.WriteLine($"latest   {info.Tag} -> {info.Version}");
            Console.WriteLine($"asset    {info.AssetName ?? "(none for this runtime)"}" +
                (info.AssetSize > 0 ? $" {info.AssetSize / 1_000_000.0:F1}MB" : ""));
            Console.WriteLine(
                $"verdict  {(info.Version > Update.UpdateChecker.CurrentVersion ? "update available" : "up to date")}");
            return 0;
        }
        try
        {
            SessionTurnStateTests.RunAll();
            SubagentFilterTests.RunAll();
            GrokTurnStateTests.RunAll();
            ProviderSelectionTests.RunAll();
            AgentCatalogTests.RunAll();
            UsageExhaustionAlarmTests.RunAll();
            SoloCenterLayoutTests.RunAll();
            CodexReplayGuardTests.RunAll();
            UpdateCheckerTests.RunAll();
            UsageCachePolicyTests.RunAll();
            ReminderDeliveryKeyTests.RunAll();
            SecurityGuardTests.RunAll();
            TurnAlarmNavigatorTests.RunAll();
            FormattingTests.RunAll();
            ScannerCwdTests.RunAll();
            CodexPayloadParsingTests.RunAll();
            AntigravityQuotaTests.RunAll();
            SteppedMeterTests.RunAll();
            ProviderLogoAnimationTests.RunAll();
            BrandGeometryTests.RunAll();
            Console.WriteLine("ALL GREEN");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"RED: {error.Message}");
            return 1;
        }
    }

    private static int LiveScan()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var now = DateTimeOffset.UtcNow;
        var sessions = SessionScanner.MonitoringScan(now, new Dictionary<string, DateTimeOffset>());
        Console.WriteLine($"{sessions.Count} session(s) discovered");
        foreach (var session in sessions.Take(15))
        {
            var age = now - session.Modified;
            Console.WriteLine(
                $"[{session.Tool,-6}] {session.Status,-12} age={age.TotalSeconds,8:F0}s  " +
                $"{session.Label} ({session.SessionId[..Math.Min(8, session.SessionId.Length)]}) " +
                $"target={session.LaunchTarget}");
        }
        return 0;
    }
}
