using AgentIsland.Model;

namespace AgentIsland.Tests;

/// Pins the island's two-slot selection rules. The silhouette geometry is two
/// tabs and two pills, so a third pick must be REFUSED rather than silently
/// evicting an earlier one, and a persisted list must survive an unknown or
/// duplicated entry without ever handing back more than two providers.
public static class ProviderSelectionTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("sanitize keeps user order", TestSanitizeOrders),
            ("sanitize drops unknown and duplicate entries", TestSanitizeCleans),
            ("sanitize caps at two", TestSanitizeCaps),
            ("sanitize of null is full/empty", TestSanitizeNull),
            ("toggle on adds in user order", TestToggleAdds),
            ("toggle off removes", TestToggleRemoves),
            ("third pick is refused, nothing evicted", TestThirdPickRefused),
            ("toggle off at the cap always succeeds", TestToggleOffAtCap),
            ("migration carries the pre-slot pair over", TestMigration),
            ("all five agents parse and roundtrip correctly", TestAllFiveAgentsRoundtrip),
            ("custom provider pairings sanitize and persist correctly", TestCustomProviderPairings),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("ProviderSelectionTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string Spell(IEnumerable<DisplayProvider> providers) =>
        string.Join(",", providers.Select(p => p.RawValue()));

    private static void TestSanitizeOrders()
    {
        var result = ProviderSelection.SanitizeOrder(new[] { "codex", "claude" });
        Expect(Spell(result) == "codex,claude,antigravity,grok,cursor", $"user order not preserved: {Spell(result)}");
    }

    private static void TestSanitizeCleans()
    {
        var result = ProviderSelection.SanitizeOrder(new[] { "grok", "nope", "grok", "" });
        Expect(Spell(result) == "grok,claude,codex,antigravity,cursor", $"unknown/duplicate entries survived or missing omitted: {Spell(result)}");
    }

    private static void TestSanitizeCaps()
    {
        var order = ProviderSelection.SanitizeOrder(new[] { "cursor", "grok", "antigravity", "codex", "claude" });
        var enabled = ProviderSelection.SanitizeEnabled(new[] { "cursor", "grok", "antigravity", "codex", "claude" }, order);
        Expect(enabled.Count == ProviderSelection.MaxEnabled,
            $"cap of {ProviderSelection.MaxEnabled} not enforced: {Spell(enabled)}");
        Expect(Spell(enabled) == "cursor,grok", $"cap kept the wrong two: {Spell(enabled)}");
    }

    private static void TestSanitizeNull()
    {
        Expect(ProviderSelection.SanitizeOrder(null).Count == 5, "a missing key must decode to a full order");
        Expect(ProviderSelection.SanitizeEnabled(null, new List<DisplayProvider>()).Count == 0, "a missing key must decode to an empty selection");
    }

    private static void TestToggleAdds()
    {
        var current = new List<DisplayProvider> { DisplayProvider.Cursor };
        var order = new List<DisplayProvider> { DisplayProvider.Claude, DisplayProvider.Cursor };
        Expect(ProviderSelection.TryToggle(current, DisplayProvider.Claude, order, out var next),
            "adding a second provider must succeed");
        Expect(Spell(next) == "claude,cursor", $"added out of user order: {Spell(next)}");
    }

    private static void TestToggleRemoves()
    {
        var current = new List<DisplayProvider> { DisplayProvider.Claude, DisplayProvider.Grok };
        var order = new List<DisplayProvider> { DisplayProvider.Claude, DisplayProvider.Grok };
        Expect(ProviderSelection.TryToggle(current, DisplayProvider.Claude, order, out var next),
            "removing an occupant must succeed");
        Expect(Spell(next) == "grok", $"wrong provider removed: {Spell(next)}");
    }

    private static void TestThirdPickRefused()
    {
        var current = new List<DisplayProvider> { DisplayProvider.Claude, DisplayProvider.Codex };
        var order = new List<DisplayProvider> { DisplayProvider.Claude, DisplayProvider.Codex, DisplayProvider.Grok };
        Expect(!ProviderSelection.TryToggle(current, DisplayProvider.Grok, order, out var next),
            "a third pick must be refused");
        Expect(Spell(next) == "claude,codex",
            $"a refused toggle must leave the selection untouched: {Spell(next)}");
    }

    private static void TestToggleOffAtCap()
    {
        var current = new List<DisplayProvider> { DisplayProvider.Antigravity, DisplayProvider.Cursor };
        var order = new List<DisplayProvider> { DisplayProvider.Antigravity, DisplayProvider.Cursor, DisplayProvider.Codex };
        Expect(ProviderSelection.TryToggle(current, DisplayProvider.Cursor, order, out var next),
            "turning one off while full must succeed");
        Expect(Spell(next) == "antigravity", $"wrong result at the cap: {Spell(next)}");
    }

    private static void TestMigration()
    {
        Expect(Spell(ProviderSelection.Migrated(true, true)) == "claude,codex",
            "both pre-slot toggles on must migrate to both slots");
        Expect(Spell(ProviderSelection.Migrated(false, true)) == "codex",
            "a Codex-only island must migrate unchanged");
        Expect(ProviderSelection.Migrated(false, false).Count == 0,
            "an empty pre-slot island must migrate to an empty selection");
    }

    private static void TestAllFiveAgentsRoundtrip()
    {
        var all = new[]
        {
            DisplayProvider.Claude,
            DisplayProvider.Codex,
            DisplayProvider.Antigravity,
            DisplayProvider.Grok,
            DisplayProvider.Cursor,
        };
        foreach (var p in all)
        {
            var raw = p.RawValue();
            var parsed = DisplayProviders.Parse(raw);
            Expect(parsed == p, $"Failed roundtrip for {p}: raw={raw}, parsed={parsed}");
        }
        Expect(DisplayProviders.Parse("gemini") == DisplayProvider.Antigravity, "gemini legacy name must resolve to antigravity");
    }

    private static void TestCustomProviderPairings()
    {
        var order = ProviderSelection.SanitizeOrder(new[] { "antigravity", "codex", "grok", "cursor", "claude" });
        var enabled = ProviderSelection.SanitizeEnabled(new[] { "antigravity", "codex" }, order);
        Expect(Spell(enabled) == "antigravity,codex", $"Antigravity + Codex pairing failed: {Spell(enabled)}");

        var grokCursor = ProviderSelection.SanitizeEnabled(new[] { "cursor", "grok" }, order);
        Expect(Spell(grokCursor) == "grok,cursor", $"Grok + Cursor pairing failed: {Spell(grokCursor)}");
    }
}
