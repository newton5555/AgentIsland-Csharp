using System.IO;
using System.Text.Json;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.Codex;

namespace AgentIsland.Tests;

/// Locks current CodexLogParser totals on the P0 fixtures and records where
/// the P2 target differs. Target counts are not implemented here.
public class P0CodexBaselineTests
{
    [Fact]
    public void CurrentParser_MatchesFixtureExpectations()
    {
        foreach (var testCase in LoadCases())
        {
            var actual = ParseFiles(testCase.Files);
            Assert.Equal(testCase.Current.Events, actual.Count);
            Assert.Equal(testCase.Current.Input, actual.Sum(item => item.InputTokens));
            Assert.Equal(testCase.Current.Output, actual.Sum(item => item.OutputTokens));
        }
    }

    [Fact]
    public void KeepCases_CurrentEqualsTarget()
    {
        foreach (var testCase in LoadCases().Where(item => item.Keep))
        {
            Assert.Equal(testCase.Target.Events, testCase.Current.Events);
            Assert.Equal(testCase.Target.Input, testCase.Current.Input);
            Assert.Equal(testCase.Target.Output, testCase.Current.Output);
        }
    }

    [Fact]
    public void AdjustedCases_CurrentDiffersFromTarget()
    {
        var adjusted = LoadCases().Where(item => !item.Keep).ToList();
        Assert.Equal(3, adjusted.Count);
        foreach (var testCase in adjusted)
        {
            Assert.True(
                testCase.Current.Events != testCase.Target.Events
                || testCase.Current.Input != testCase.Target.Input
                || testCase.Current.Output != testCase.Target.Output,
                testCase.Id + " should remain a P2 gap");
        }
    }

    [Fact]
    public void ReasoningSample_DoesNotAddReasoningOnTopOfOutput()
    {
        var events = CodexLogParser.ParseFile(CodexFile("06-reasoning-in-output.jsonl"));
        Assert.Single(events);
        Assert.Equal(20, events[0].OutputTokens);
        Assert.Equal(40, events[0].InputTokens);
        Assert.Equal(60, events[0].BillableTokens);
    }

    private static IReadOnlyList<TokenEvent> ParseFiles(IReadOnlyList<string> files)
    {
        var output = new List<TokenEvent>();
        foreach (var file in files)
        {
            output.AddRange(CodexLogParser.ParseFile(CodexFile(file)));
        }
        return output;
    }

    private static IReadOnlyList<FixtureCase> LoadCases()
    {
        var path = CodexFile("expected.json");
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<FixtureFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("P0 Codex expected.json is empty.");
        Assert.Equal(6, document.Cases.Count);
        return document.Cases;
    }

    private static string CodexFile(string relative)
    {
        var parts = new List<string> { AppContext.BaseDirectory, "Fixtures", "P0", "Codex" };
        parts.AddRange(relative.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(parts.ToArray());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record FixtureFile(string ParserVersion, List<FixtureCase> Cases);
    private sealed record FixtureCase(
        string Id,
        List<string> Files,
        bool Keep,
        FixtureTotals Current,
        FixtureTotals Target);
    private sealed record FixtureTotals(int Events, long Input, long Output);
}
