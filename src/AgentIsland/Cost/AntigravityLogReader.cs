namespace AgentIsland.Cost;

/// Antigravity ships no local token ledger (verified on a real install,
/// 2026-08-08: `brain/` transcripts carry no usage records, `cache/` holds
/// only onboarding and conversation metadata). The honest cost story is
/// therefore "no local data" — CostPage renders the cold face, never a
/// fabricated $0.
///
/// This stub is the seam: if a future Antigravity version starts writing
/// per-turn token counts, parse them here into TokenEvents and the whole
/// cost pipeline lights up unchanged.
public static class AntigravityLogReader
{
    public static List<TokenEvent> Scan(int lookbackDays) => new();
}
