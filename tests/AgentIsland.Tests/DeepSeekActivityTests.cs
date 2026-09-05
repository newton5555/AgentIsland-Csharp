using System.Text.Json;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Core;
using AgentIsland.Providers.Sessions.DeepSeek;

namespace AgentIsland.Tests;

/// Contract tests for the DeepSeek Harness activity projection.
/// Activity is deliberately separate from token accounting: any streaming
/// event makes a session Working, while a final assistant message/turn
/// boundary makes it complete, and the latest route wins for mixed sessions.
public class DeepSeekActivityTests
{
    [Fact]
    public void TestDeepSeekActivity() => RunAll();

    internal static void RunAll()
    {
        TestOfficialActiveStream();
        TestOfficialCompletedTurn();
        TestLatestRouteKeepsEveryGatewayEligible();
        TestSubagentMetadataIsMarked();
        TestProjectFolderDecoding();
        TestActivityMonitorPublishesDeepSeekState();
        Console.WriteLine("DeepSeekActivityTests GREEN");
    }

    private static void TestOfficialActiveStream()
    {
        var lines = new[]
        {
            Session(@"F:\Projects\Demo-Repo"),
            Header("deepseek-official", "deepseek-chat", 1_700_000_000_000),
            Activity("turn/start", 1_700_000_000_010, 7, 2),
            Activity("tool/call", 1_700_000_000_020, 7, 2),
        };

        var snapshot = DeepSeekActivityParser.ParseLines(lines);
        Expect(snapshot is not null, "official activity stream produces a snapshot");
        Expect(snapshot!.IsOfficialRoute, "official activity stream is accepted");
        Expect(snapshot.LatestKind == DeepSeekActivityKind.Active,
            "turn/tool events classify an official session as active");
        Expect(snapshot.LatestTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_020),
            "latest activity timestamp is retained");
        Expect(snapshot.TurnKey == "7:2", "turn and step form a stable activity key");
        Expect(snapshot.Model == "deepseek-chat", "header model is retained for activity");
        Expect(snapshot.Cwd == @"F:\Projects\Demo-Repo",
            "session metadata preserves the authoritative working directory");
        Console.WriteLine("PASS DeepSeek official active activity projection");
    }

    private static void TestOfficialCompletedTurn()
    {
        var lines = new[]
        {
            Header("deepseek-official", "deepseek-reasoner", 1_700_000_001_000),
            Activity("turn/start", 1_700_000_001_010, 8, 1),
            AssistantMessage(1_700_000_001_030, 8, 1,
                "deepseek-official", "deepseek-reasoner"),
        };

        var snapshot = DeepSeekActivityParser.ParseLines(lines);
        Expect(snapshot is not null && snapshot.IsOfficialRoute,
            "completed official turn remains on the official route");
        Expect(snapshot!.LatestKind == DeepSeekActivityKind.Completed,
            "assistant message classifies a turn as completed");
        Expect(snapshot.LatestTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_030),
            "completed turn uses the final assistant timestamp");
        Expect(snapshot.Model == "deepseek-reasoner", "assistant source model is retained");
        Console.WriteLine("PASS DeepSeek completed turn projection");
    }

    private static void TestLatestRouteKeepsEveryGatewayEligible()
    {
        var lines = new[]
        {
            Header("deepseek-official", "deepseek-chat", 1_700_000_002_000),
            Activity("turn/start", 1_700_000_002_010, 9, 1),
            Header("my-gateway", "deepseek-chat", 1_700_000_002_020),
            Activity("turn/start", 1_700_000_002_030, 10, 1),
        };

        var snapshot = DeepSeekActivityParser.ParseLines(lines);
        Expect(snapshot is not null, "mixed route stream still parses safely");
        Expect(!snapshot!.IsOfficialRoute,
            "the mixed stream retains its non-official route metadata");
        Expect(snapshot.LatestProvider == "my-gateway",
            "latest route metadata wins over an older official header");
        Expect(DeepSeekActivityReader.IsEligibleForActivity(snapshot!),
            "a session most recently using my-gateway still drives whale activity");
        Console.WriteLine("PASS DeepSeek all-gateway activity eligibility");
    }

    private static void TestProjectFolderDecoding()
    {
        var path = @"C:\Users\newto\.dsh\sessions\--F-Projects-LLRP-LLRPCSharp--\session-1\session.jsonl.zstd";
        var project = AgentIsland.Backend.Monitoring.DeepSeekActivityReader.ProjectFromSessionPath(path);
        Expect(project == @"F:\Projects\LLRP\LLRPCSharp",
            "DSH project folder is decoded for the display label");
        Console.WriteLine("PASS DeepSeek project label decoding");
    }

    private static void TestSubagentMetadataIsMarked()
    {
        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                type = "session",
                origin = "subagent",
                delegationDepth = 1,
            }),
            Header("deepseek-official", "deepseek-chat", 1_700_000_003_000),
            Activity("turn/start", 1_700_000_003_010, 11, 1),
        };

        var snapshot = DeepSeekActivityParser.ParseLines(lines);
        Expect(snapshot is not null && snapshot.IsSubagent,
            "delegated DSH sessions are marked as subagents for monitor filtering");
        Console.WriteLine("PASS DeepSeek subagent metadata is recognized");
    }

    private static void TestActivityMonitorPublishesDeepSeekState()
    {
        var now = DateTimeOffset.UtcNow;
        var path = @"C:\Users\newto\.dsh\sessions\monitor-test\session.jsonl.zstd";
        var session = new ScannedSession(
            TriggerTool.DeepSeek,
            "monitor-test",
            @"F:\Projects\Demo-Repo",
            "Demo-Repo",
            now.AddSeconds(-1),
            ActivityState.Working,
            path,
            "deepseek:1:1",
            SessionLaunchTarget.Cli);

        ActivityMonitor.Shared.Apply(new List<ScannedSession> { session }, now);
        Expect(ActivityMonitor.Shared.RawStateFor(TriggerTool.DeepSeek) == ActivityState.Working,
            "DeepSeek monitor publishes the raw working state");
        Expect(ActivityMonitor.Shared.DeepSeek == ActivityState.Working,
            "DeepSeek named monitor property follows the working state");

        // Do not leave the singleton in a live state for the following UI
        // tests; a real scan will repopulate it on the next timer tick.
        ActivityMonitor.Shared.Apply(new List<ScannedSession>(), now);
        Console.WriteLine("PASS DeepSeek activity monitor publishes working state");
    }

    private static string Header(string provider, string model, long time) => JsonSerializer.Serialize(new
    {
        type = "request/header",
        time,
        data = new
        {
            header = new
            {
                config = new { provider, model },
            },
        },
    });

    private static string Session(string cwd) => JsonSerializer.Serialize(new
    {
        type = "session",
        cwd,
        delegationDepth = 0,
    });

    private static string Activity(string type, long time, long turn, long step) =>
        JsonSerializer.Serialize(new
        {
            type,
            time,
            data = new { turn, step },
        });

    private static string AssistantMessage(
        long time,
        long turn,
        long step,
        string provider,
        string model) => JsonSerializer.Serialize(new
        {
            type = "assistant/message",
            time,
            data = new
            {
                turn,
                step,
                message = new
                {
                    role = "assistant",
                    source = new { kind = "model", provider, model },
                },
            },
        });

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
