using AgentIsland.Core.Agents;

namespace AgentIsland.Core.Cost;

/// Resume point for incremental collection. ParserVersion changes force a
/// rebuild of that source even when Position is unchanged.
public sealed record ScanCursor(
    AgentKey Agent,
    string SourceIdentity,
    long Position,
    string ParserVersion,
    long Size,
    long MtimeUtcTicks,
    string? ContentFingerprint);
