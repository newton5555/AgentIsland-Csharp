using AgentIsland.Alarm;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

/// Pins the shell-safety guards that stand between attacker-plantable
/// on-disk session metadata and cmd.exe: a bad session id or resume message
/// must be rejected, never executed or silently mangled.
public static class SecurityGuardTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("navigator sanitize rejects, never strips", TestSanitizeRejectsRatherThanStrips),
            ("account labels cannot escape the store directory", TestAccountLabelSanitizing),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("SecurityGuardTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }



    private static void TestSanitizeRejectsRatherThanStrips()
    {
        // Stripping would resume a DIFFERENT, plausible id — Sanitize must
        // return empty for anything impure, not a cleaned-up variant.
        Expect(TurnAlarmNavigator.Sanitize("x&calc&") == "", "Sanitize must reject, not strip");
        Expect(TurnAlarmNavigator.Sanitize("good-id_123") == "good-id_123", "clean id must pass through");
    }



    /// A saved-account label becomes a FILENAME holding a live credential
    /// pair, so it must never carry a separator, a traversal segment, or a
    /// reserved DOS device name. Unlike a session id it is cosmetic, so this
    /// one strips rather than rejects.
    private static void TestAccountLabelSanitizing()
    {
        var cases = new (string? Raw, string Expected)[]
        {
            ("work", "work"),
            ("work / personal", "work  personal"),
            (@"..\..\etc", "etc"),
            (@"a/b\c", "abc"),
            ("CON", "CON_"),
            ("com1", "com1_"),
            ("  ", ""),
            ("", ""),
            (null, ""),
        };
        foreach (var (raw, expected) in cases)
        {
            var actual = CodexAccountSwitcher.Sanitize(raw);
            Expect(actual == expected,
                $"Sanitize(\"{raw}\") = \"{actual}\", expected \"{expected}\"");
        }

        // The 40-character cap runs BEFORE the trim, so a label that ends on
        // a space at the boundary must not keep it: Win32 drops a trailing
        // space from a filename, and two labels would then fight over one file.
        var long40 = CodexAccountSwitcher.Sanitize(new string('a', 60));
        Expect(long40.Length == 40, $"label cap not applied: {long40.Length} chars");
        var boundarySpace = CodexAccountSwitcher.Sanitize(new string('a', 39) + "   tail");
        Expect(boundarySpace == new string('a', 39),
            $"a trailing space survived the cap: \"{boundarySpace}\"");
    }
}
