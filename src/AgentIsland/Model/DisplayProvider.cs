using AgentIsland.Core;

namespace AgentIsland.Model;

/// Display-layer provider identity for the island's two silhouette slots.
/// Strictly a presentation concept: full monitoring (usage windows, turn
/// alarms, attention states) stays with Claude and Codex — the guest slots
/// are usage badges, logo + quota pill and nothing deeper.
public enum DisplayProvider
{
    Claude,
    Codex,
    Antigravity,
    Grok,
    Cursor,
}

public static class DisplayProviders
{
    /// Declaration order is canonical slot order: Claude before Codex before
    /// the guests, matching the pre-slot fixed layout so migrated users see
    /// zero movement.
    public static readonly DisplayProvider[] All =
    {
        DisplayProvider.Claude,
        DisplayProvider.Codex,
        DisplayProvider.Antigravity,
        DisplayProvider.Grok,
        DisplayProvider.Cursor,
    };

    /// The two providers that carry the full monitoring stack.
    public static readonly DisplayProvider[] FullyMonitored =
    {
        DisplayProvider.Claude,
        DisplayProvider.Codex,
    };

    /// Quota-badge providers, in the order the panel appends their rows.
    public static readonly DisplayProvider[] Guests =
    {
        DisplayProvider.Antigravity,
        DisplayProvider.Grok,
        DisplayProvider.Cursor,
    };

    public static DisplayProvider? Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "claude" => DisplayProvider.Claude,
        "codex" => DisplayProvider.Codex,
        "antigravity" => DisplayProvider.Antigravity,
        // Legacy spelling from the Gemini era — persisted slot picks keep
        // resolving to the same seat.
        "gemini" => DisplayProvider.Antigravity,
        "grok" => DisplayProvider.Grok,
        "cursor" => DisplayProvider.Cursor,
        _ => null,
    };
}

public static class DisplayProviderExtensions
{
    /// Persisted spelling. Stays lowercase and stable — it is the on-disk
    /// form of the slot selection.
    public static string RawValue(this DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => "claude",
        DisplayProvider.Codex => "codex",
        DisplayProvider.Antigravity => "antigravity",
        DisplayProvider.Grok => "grok",
        DisplayProvider.Cursor => "cursor",
        _ => provider.ToString().ToLowerInvariant(),
    };

    public static string DisplayName(this DisplayProvider provider) => ProviderIdentity.DisplayName(provider);

    /// Full monitoring = official usage windows + transcript activity +
    /// approaching-limit alerts. Badge-only providers report quota and stop.
    public static bool HasFullMonitoring(this DisplayProvider provider) =>
        provider is DisplayProvider.Claude or DisplayProvider.Codex;

    /// Which flank the logo holds in the solo layout — the pill crosses to
    /// the opposite side. Claude's historical home is the left tab, Codex's
    /// the right; the guests get stable assignments so the layout never
    /// flips between launches.
    public static bool SoloLogoFlankIsLeading(this DisplayProvider provider) =>
        provider is DisplayProvider.Claude or DisplayProvider.Antigravity or DisplayProvider.Cursor;

    /// Position in canonical slot order; the enum's declaration order is it.
    public static int SlotOrder(this DisplayProvider provider) => (int)provider;

    public static TriggerTool ToTriggerTool(this DisplayProvider provider) => provider switch
    {
        DisplayProvider.Claude => TriggerTool.Claude,
        DisplayProvider.Codex => TriggerTool.Codex,
        DisplayProvider.Antigravity => TriggerTool.Antigravity,
        DisplayProvider.Grok => TriggerTool.Grok,
        DisplayProvider.Cursor => TriggerTool.Cursor,
        _ => TriggerTool.Claude,
    };

    public static DisplayProvider ToDisplayProvider(this TriggerTool tool) => tool switch
    {
        TriggerTool.Claude => DisplayProvider.Claude,
        TriggerTool.Codex => DisplayProvider.Codex,
        TriggerTool.Antigravity => DisplayProvider.Antigravity,
        TriggerTool.Grok => DisplayProvider.Grok,
        TriggerTool.Cursor => DisplayProvider.Cursor,
        _ => DisplayProvider.Claude,
    };
}

/// Pure selection rules for the island's two slots — no singleton, no
/// Preferences — so the max-2 constraint, the legacy migration and the
/// persistence round-trip stay testable on their own.
public static class ProviderSelection
{
    public const int MaxEnabled = 2;

    public static List<DisplayProvider> SanitizeOrder(IEnumerable<string>? rawOrder)
    {
        var result = new List<DisplayProvider>();
        foreach (var value in rawOrder ?? Array.Empty<string>())
        {
            if (DisplayProviders.Parse(value) is { } provider && !result.Contains(provider))
            {
                result.Add(provider);
            }
        }
        foreach (var provider in DisplayProviders.All)
        {
            if (!result.Contains(provider)) result.Add(provider);
        }
        return result;
    }

    public static List<DisplayProvider> SanitizeEnabled(IEnumerable<string>? rawEnabled, IReadOnlyList<DisplayProvider> order)
    {
        var enabledSet = new HashSet<DisplayProvider>();
        foreach (var value in rawEnabled ?? Array.Empty<string>())
        {
            if (DisplayProviders.Parse(value) is { } provider)
            {
                enabledSet.Add(provider);
            }
        }
        return order.Where(enabledSet.Contains).Take(MaxEnabled).ToList();
    }

    public static bool TryToggle(
        IReadOnlyList<DisplayProvider> currentEnabled,
        DisplayProvider provider,
        IReadOnlyList<DisplayProvider> order,
        out List<DisplayProvider> nextEnabled)
    {
        if (currentEnabled.Contains(provider))
        {
            nextEnabled = currentEnabled.Where(item => item != provider).ToList();
            return true;
        }
        if (currentEnabled.Count >= MaxEnabled)
        {
            nextEnabled = currentEnabled.ToList();
            return false;
        }
        var nextSet = new HashSet<DisplayProvider>(currentEnabled) { provider };
        nextEnabled = order.Where(nextSet.Contains).ToList();
        return true;
    }

    public static List<DisplayProvider> Migrated(bool claudeVisible, bool codexVisible)
    {
        var result = new List<DisplayProvider>();
        if (claudeVisible) result.Add(DisplayProvider.Claude);
        if (codexVisible) result.Add(DisplayProvider.Codex);
        return result;
    }
}
