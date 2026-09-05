using AgentIsland.Core;
using AgentIsland.Providers.Cost.Cursor;

namespace AgentIsland.Backend.Cost;

/// Windows host adapter for Cursor's native database reader. The database
/// access lives in AgentIsland.Windows; this seam binds it to Cursor's pure
/// bubble parser and preserves the existing CostStore surface.
public static class CursorLogReader
{
    public static List<TokenEvent> Scan(int lookbackDays, CancellationToken cancellationToken = default) =>
        CursorDatabaseReader.Scan(lookbackDays, CursorLogParser.ParseBubble, cancellationToken);

    // Existing diagnostics can still exercise the app seam while the
    // provider payload parser lives in AgentIsland.Providers.
    internal static TokenEvent? ParseBubble(string json, DateTimeOffset fallback) =>
        CursorLogParser.ParseBubble(json, fallback);
}
