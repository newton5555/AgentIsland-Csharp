using AgentIsland.Core;

namespace AgentIsland.Tests;

/// Pins the subagent suppression contract: fan-out workers (Claude sidechain
/// lines / agent transcripts, Codex subagent rollouts) must never classify or
/// alarm as the user's own turn. There is no opt-in any more — the Settings
/// toggle and its store are gone, and the scanner drops subagent transcripts
/// unconditionally (macOS parity). The ClaudeAgent cases below still pin the
/// classifier itself, which is what the scanner's skip rule is built on.
public static class SubagentFilterTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("interleaved sidechain end_turn does not finish the main turn", TestSidechainEndTurnIgnoredInMainTranscript),
            ("main end_turn stays done past trailing sidechain lines", TestMainEndTurnSurvivesTrailingSidechain),
            ("agent transcript end_turn is done", TestAgentTranscriptEndTurnIsDone),
            ("agent transcript toolEndsTurn result is done", TestAgentTranscriptToolEndsTurnIsDone),
            ("agent transcript plain tool result is still running", TestAgentTranscriptPlainToolResultIsRunning),
            ("codex cli source is interactive", TestCodexCliSourceInteractive),
            ("codex missing source is interactive", TestCodexMissingSourceInteractive),
            ("codex subagent source is subagent", TestCodexSubagentSourceDetected),
            ("codex thread_spawn subagent is subagent", TestCodexThreadSpawnDetected),
            ("codex internal source is automation", TestCodexInternalSourceAutomation),
            ("codex exec source is automation", TestCodexExecSourceAutomation),
            ("codex foreign originator is automation", TestCodexForeignOriginatorAutomation),
            ("codex foreign originator outranks subagent source", TestForeignOriginatorOutranksSubagentSource),
            ("subagent transcript path shapes are classified", TestSubagentTranscriptPathShapes),
        };

        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SubagentFilterTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // MARK: - Claude turn state vs sidechain lines

    private static void TestSidechainEndTurnIgnoredInMainTranscript()
    {
        // Older Claude Code interleaves subagent lines into the main
        // transcript. A subagent finishing mid-fan-out must not read as
        // "the user is up" while the main turn is still running.
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","timestamp":"2026-07-09T10:00:00.000Z","message":{"role":"user","content":"go"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-07-09T10:00:05.000Z","message":{"stop_reason":"tool_use"}}""",
            """{"type":"assistant","uuid":"side1","isSidechain":true,"timestamp":"2026-07-09T10:00:30.000Z","message":{"stop_reason":"end_turn"}}""",
        };
        var state = SessionTurnState.Claude(lines);
        Expect(!state.IsDone, "a sidechain end_turn must not finish the main turn");
        Expect(state.Key == "a1", "turn key must come from the last MAIN line, not the sidechain");
    }

    private static void TestMainEndTurnSurvivesTrailingSidechain()
    {
        var lines = new[]
        {
            """{"type":"assistant","uuid":"a9","timestamp":"2026-07-09T10:05:00.000Z","message":{"stop_reason":"end_turn"}}""",
            """{"type":"user","uuid":"side2","isSidechain":true,"timestamp":"2026-07-09T10:05:02.000Z","message":{"role":"user","content":"tool result"}}""",
        };
        var state = SessionTurnState.Claude(lines);
        Expect(state.IsDone, "trailing sidechain lines must not hide a finished main turn");
        Expect(state.Key == "a9", "the main assistant line keeps the turn key");
    }

    private static void TestAgentTranscriptEndTurnIsDone()
    {
        var lines = new[]
        {
            """{"type":"assistant","uuid":"a1","isSidechain":true,"timestamp":"2026-07-09T10:00:05.000Z","message":{"stop_reason":"end_turn"}}""",
        };
        var state = SessionTurnState.ClaudeAgent(lines);
        Expect(state.IsDone, "an agent transcript's own end_turn means the agent finished");
    }

    private static void TestAgentTranscriptToolEndsTurnIsDone()
    {
        // Most agent runs end on the final tool result (StructuredOutput),
        // which the harness marks toolEndsTurn — not on an assistant stop.
        var lines = new[]
        {
            """{"type":"assistant","uuid":"a1","isSidechain":true,"timestamp":"2026-07-09T10:00:05.000Z","message":{"stop_reason":"tool_use"}}""",
            """{"type":"user","uuid":"u2","isSidechain":true,"toolEndsTurn":true,"timestamp":"2026-07-09T10:00:09.000Z","message":{"role":"user","content":"result"}}""",
        };
        var state = SessionTurnState.ClaudeAgent(lines);
        Expect(state.IsDone, "a toolEndsTurn result must finish the agent turn");
        Expect(state.Key == "u2", "the closing tool result carries the turn key");
    }

    private static void TestAgentTranscriptPlainToolResultIsRunning()
    {
        var lines = new[]
        {
            """{"type":"assistant","uuid":"a1","isSidechain":true,"timestamp":"2026-07-09T10:00:05.000Z","message":{"stop_reason":"tool_use"}}""",
            """{"type":"user","uuid":"u2","isSidechain":true,"timestamp":"2026-07-09T10:00:09.000Z","message":{"role":"user","content":"result"}}""",
        };
        var state = SessionTurnState.ClaudeAgent(lines);
        Expect(!state.IsDone, "an ordinary tool result mid-run must stay running");
    }

    // MARK: - Codex rollout classification

    private static SessionScanner.CodexRolloutKind Classify(string meta)
    {
        var parsed = SessionScanner.ParseCodexMeta(meta);
        Expect(parsed is not null, $"meta line should parse: {meta}");
        return parsed!.Value.Kind;
    }

    private static void TestCodexCliSourceInteractive()
    {
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"codex_cli_rs","source":"cli"}}""")
            == SessionScanner.CodexRolloutKind.Interactive,
            "source \"cli\" is the user's own thread");
    }

    private static void TestCodexMissingSourceInteractive()
    {
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"codex_cli_rs"}}""")
            == SessionScanner.CodexRolloutKind.Interactive,
            "old CLIs without a source field stay interactive");
    }

    private static void TestCodexSubagentSourceDetected()
    {
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"codex_cli_rs","source":{"subagent":"review"}}}""")
            == SessionScanner.CodexRolloutKind.Subagent,
            "a review subagent must classify as subagent");
    }

    private static void TestCodexThreadSpawnDetected()
    {
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"codex_cli_rs","source":{"subagent":{"thread_spawn":{"parent_thread_id":"p1","depth":1}}}}}""")
            == SessionScanner.CodexRolloutKind.Subagent,
            "a spawned agent thread must classify as subagent");
    }

    private static void TestCodexInternalSourceAutomation()
    {
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"codex_cli_rs","source":{"internal":"memory_consolidation"}}}""")
            == SessionScanner.CodexRolloutKind.Automation,
            "internal machinery must classify as automation");
    }

    private static void TestCodexExecSourceAutomation()
    {
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"codex_cli_rs","source":"exec"}}""")
            == SessionScanner.CodexRolloutKind.Automation,
            "codex exec runs must classify as automation");
    }

    private static void TestCodexForeignOriginatorAutomation()
    {
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"scs-probe"}}""")
            == SessionScanner.CodexRolloutKind.Automation,
            "foreign originators must classify as automation");
    }

    private static void TestForeignOriginatorOutranksSubagentSource()
    {
        // An orchestrator app spawning codex threads: hard-skip wins so the
        // rollout can never alarm, opted-in or not.
        Expect(Classify(
            """{"type":"session_meta","payload":{"id":"s1","cwd":"C:\\p","originator":"some-orchestrator","source":{"subagent":"review"}}}""")
            == SessionScanner.CodexRolloutKind.Automation,
            "a foreign originator must outrank the subagent source");
    }

    // MARK: - Claude transcript path classification

    private static void TestSubagentTranscriptPathShapes()
    {
        var cases = new (string Path, bool Expected)[]
        {
            (@"enc-project\11111111-2222-3333-4444-555555555555.jsonl", false),
            (@"enc-project\11111111-2222-3333-4444-555555555555\subagents\agent-abc123.jsonl", true),
            ("enc-project/11111111-2222-3333-4444-555555555555/subagents/agent-abc123.jsonl", true),
            (@"enc-project\agent-abc123.jsonl", true),
            (@"enc-project\subagents\11111111-2222-3333-4444-555555555555.jsonl", true),
            (@"agent-island-project\11111111-2222-3333-4444-555555555555.jsonl", false),
        };
        foreach (var (path, expected) in cases)
        {
            Expect(SessionScanner.IsClaudeSubagentTranscript(path) == expected,
                $"IsClaudeSubagentTranscript(\"{path}\") should be {expected}");
        }
    }
}
