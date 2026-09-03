using System.ComponentModel;
using System.IO;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Usage;

namespace AgentIsland.Model;

/// Which providers occupy the island's two silhouette slots, plus per-
/// provider machine detection.
///
/// The silhouette geometry is fixed at two tabs + two pills, so provider
/// choice BINDS providers to those slots and never adds a third: turning a
/// third one on is refused outright — never a silent eviction of an older
/// pick.
///
/// Two layers, mirroring macOS:
///   - the manual selection (Toggle / ClaudeVisible / CodexVisible);
///   - machine detection, probed once at launch. A claude/codex with no CLI
///     footprint auto-yields its half of the island so single-subscription
///     users get the solo layout without hunting for a Settings toggle.
///     Manual wins once touched: flipping a toggle records intent, and
///     detection stops second-guessing that provider.
///   - gemini/grok/cursor are zero-intrusion: no login on this machine means
///     no slot and no panel row, whatever the selection says.
public sealed class ProviderVisibilityStore : INotifyPropertyChanged
{
    private const string EnabledKey = "AgentIsland.enabledProviders.v1";
    private const string OrderKey = "AgentIsland.providerOrder.v1";
    private const string ClaudeKey = "AgentIsland.claudeVisible";
    private const string CodexKey = "AgentIsland.codexVisible";
    private const string ClaudeTouchedKey = "AgentIsland.claudeVisibleTouched";
    private const string CodexTouchedKey = "AgentIsland.codexVisibleTouched";

    public static ProviderVisibilityStore Shared { get; } = new();

    private readonly Dictionary<DisplayProvider, bool> _detected = new();
    private List<DisplayProvider> _enabled;
    private IReadOnlyList<DisplayProvider> _order;
    private IReadOnlyList<DisplayProvider> _slots;
    private bool _claudeTouched;
    private bool _codexTouched;

    public event PropertyChangedEventHandler? PropertyChanged;

    private ProviderVisibilityStore()
    {
        _claudeTouched = Preferences.Get<bool?>(ClaudeTouchedKey) == true;
        _codexTouched = Preferences.Get<bool?>(CodexTouchedKey) == true;

        if (AppEnvironment.IsDemo)
        {
            var demoProviders = DemoProviders().ToList();
            _order = ProviderSelection.SanitizeOrder(demoProviders);
            var pinned = ProviderSelection.SanitizeEnabled(demoProviders, _order);
            _enabled = pinned.Count > 0
                ? pinned
                : new List<DisplayProvider> { DisplayProvider.Claude, DisplayProvider.Codex };
        }
        else
        {
            var storedOrder = Preferences.Get<List<string>?>(OrderKey);
            var storedEnabled = Preferences.Get<List<string>?>(EnabledKey);

            if (storedOrder == null && storedEnabled != null)
            {
                // Migrate from enabledProviders.v1
                _order = ProviderSelection.SanitizeOrder(storedEnabled);
                _enabled = ProviderSelection.SanitizeEnabled(storedEnabled, _order);
            }
            else if (storedOrder != null && storedEnabled != null)
            {
                _order = ProviderSelection.SanitizeOrder(storedOrder);
                _enabled = ProviderSelection.SanitizeEnabled(storedEnabled, _order);
            }
            else if (storedOrder != null)
            {
                _order = ProviderSelection.SanitizeOrder(storedOrder);
                _enabled = new List<DisplayProvider>();
            }
            else
            {
                // First run on the slot model
                var migratedEnabled = ProviderSelection.Migrated(
                    Preferences.Get<bool?>(ClaudeKey) ?? true,
                    Preferences.Get<bool?>(CodexKey) ?? true);
                _order = ProviderSelection.SanitizeOrder(migratedEnabled.Select(p => p.RawValue()));
                _enabled = ProviderSelection.SanitizeEnabled(migratedEnabled.Select(p => p.RawValue()), _order);
            }
            Persist();
        }

        foreach (var provider in DisplayProviders.All)
        {
            _detected[provider] = AppEnvironment.IsDemo ? _enabled.Contains(provider) : Probe(provider);
        }

        _slots = ComputeSlots();
    }

    // MARK: - Selection

    public IReadOnlyList<DisplayProvider> Order => _order;

    /// The providers bound to the island slots, canonical order, max 2.
    public IReadOnlyList<DisplayProvider> Enabled => _enabled;

    public int SelectedCount => _enabled.Count;

    public bool IsEnabled(DisplayProvider provider) => _enabled.Contains(provider);

    /// Flip a provider's slot membership. Returns false when this would turn
    /// a third provider on — nothing changes and the caller explains why.
    public bool Toggle(DisplayProvider provider)
    {
        if (!ProviderSelection.TryToggle(_enabled, provider, _order, out var next)) return false;
        _enabled = next;
        MarkTouched(provider);
        Persist();
        _slots = ComputeSlots();
        RaiseAll();
        return true;
    }

    public void MoveProvider(int oldIndex, int newIndex)
    {
        var list = _order.ToList();
        var item = list[oldIndex];
        list.RemoveAt(oldIndex);
        list.Insert(newIndex, item);
        _order = list;
        // Re-evaluate enabled to match new order
        _enabled = ProviderSelection.SanitizeEnabled(_enabled.Select(p => p.RawValue()), _order);
        Persist();
        _slots = ComputeSlots();
        RaiseAll();
    }

    /// Set membership explicitly. Returns false only when turning a third
    /// provider on was refused; an already-correct value is a no-op success.
    public bool SetEnabled(DisplayProvider provider, bool enabled) =>
        IsEnabled(provider) == enabled || Toggle(provider);

    // MARK: - What the island renders

    /// Slot occupants after detection has had its say, canonical order. Left
    /// slot first, right slot second; a single entry means the solo layout.
    public IReadOnlyList<DisplayProvider> SlotProviders => _slots;

    public DisplayProvider? SoloSlotProvider => _slots.Count == 1 ? (DisplayProvider?)_slots[0] : null;

    public bool IsShown(DisplayProvider provider)
    {
        if (!_enabled.Contains(provider)) return false;
        return provider switch
        {
            // Detection only speaks for a provider whose toggle was never
            // touched, and only hides one side when the OTHER slot holds its
            // detected counterpart — a machine with neither footprint still
            // shows both instead of a blank island.
            DisplayProvider.Claude => _claudeTouched
                || IsDetected(DisplayProvider.Claude)
                || !(_enabled.Contains(DisplayProvider.Codex) && IsDetected(DisplayProvider.Codex)),
            DisplayProvider.Codex => _codexTouched
                || IsDetected(DisplayProvider.Codex)
                || !(_enabled.Contains(DisplayProvider.Claude) && IsDetected(DisplayProvider.Claude)),
            _ => IsDetected(provider),
        };
    }

    /// CLI footprint on this machine. Claude/Codex are probed once at
    /// launch; guests re-probe on the refresh cadence (see RedetectGuests).
    public bool IsDetected(DisplayProvider provider) =>
        _detected.TryGetValue(provider, out var detected) && detected;

    /// Re-probe the zero-intrusion guests (macOS redetectGuests): a login
    /// created while the app runs claims its slot on the next refresh tick,
    /// no relaunch needed. Claude/Codex keep their launch-time probe — their
    /// footprint dirs don't appear mid-session the way sign-ins do.
    public void RedetectGuests()
    {
        if (AppEnvironment.IsDemo) return;
        var changed = false;
        foreach (var provider in DisplayProviders.Guests)
        {
            var detected = Probe(provider);
            if (_detected.TryGetValue(provider, out var known) && known == detected) continue;
            _detected[provider] = detected;
            changed = true;
        }
        if (!changed) return;
        _slots = ComputeSlots();
        RaiseAll();
    }

    public bool ClaudeDetected => IsDetected(DisplayProvider.Claude);
    public bool CodexDetected => IsDetected(DisplayProvider.Codex);
    public bool AntigravityDetected => IsDetected(DisplayProvider.Antigravity);
    public bool GrokDetected => IsDetected(DisplayProvider.Grok);
    public bool CursorDetected => IsDetected(DisplayProvider.Cursor);

    /// Alert-domain reads (AlertEngine, glow attention): the manual slot
    /// pick, before detection's auto-yield. Assigning routes through the
    /// max-2 rule, so a refused third simply leaves the value where it was.
    public bool ClaudeVisible
    {
        get => IsEnabled(DisplayProvider.Claude);
        set => SetEnabled(DisplayProvider.Claude, value);
    }

    public bool CodexVisible
    {
        get => IsEnabled(DisplayProvider.Codex);
        set => SetEnabled(DisplayProvider.Codex, value);
    }

    /// The providers occupying the island's two flanks, in slot order.
    public IReadOnlyList<DisplayProvider> Slots => ComputeSlots();

    /// What the island actually renders.
    public bool ClaudeShown => IsShown(DisplayProvider.Claude);
    public bool CodexShown => IsShown(DisplayProvider.Codex);

    /// The selection is the single source of truth on every surface — the
    /// expanded panel shows exactly the slot providers, never a detected-
    /// but-unselected extra.
    public bool ClaudePanelShown => IsShown(DisplayProvider.Claude);
    public bool CodexPanelShown => IsShown(DisplayProvider.Codex);
    public bool AntigravityPanelShown => IsShown(DisplayProvider.Antigravity);
    public bool GrokPanelShown => IsShown(DisplayProvider.Grok);
    public bool CursorPanelShown => IsShown(DisplayProvider.Cursor);

    /// Quota-only rows appended to the usage page. Selection-driven, so the
    /// panel re-sizes the moment a guest slot is toggled.
    public int GuestPanelCount => DisplayProviders.Guests.Count(IsShown);

    /// Single accessor for call sites holding a TriggerTool.
    public bool IsVisible(TriggerTool tool) => IsShown(tool.ToDisplayProvider());

    // MARK: - Detection probes

    private static bool Probe(DisplayProvider provider)
    {
        var home = IslandPaths.Home;
        return provider switch
        {
            DisplayProvider.Claude => HasDir(home, ".claude") || HasDir(home, ".config", "claude"),
            DisplayProvider.Codex => HasDir(home, ".codex"),
            // Antigravity is probed by its data roots: running agy (or the
            // IDE) once creates them, and sign-in state lives in the
            // platform credential store where a presence probe proves
            // nothing about the local quota server being reachable anyway.
            DisplayProvider.Antigravity => Usage.AntigravityCredentials.Detected,
            DisplayProvider.Grok => GrokAuthFile.Exists(),
            // Cursor's editor state db is also where its session token lives,
            // so no db means no login worth a slot.
            DisplayProvider.Cursor => CursorCredentials.Exists(),
            _ => false,
        };
    }

    private static bool HasDir(params string[] parts) => Directory.Exists(Path.Combine(parts));

    private static IEnumerable<string> DemoProviders()
    {
        var raw = Environment.GetEnvironmentVariable("AGENTISLAND_DEMO_PROVIDERS");
        return string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // MARK: - Persistence

    private IReadOnlyList<DisplayProvider> ComputeSlots() => _enabled.Where(IsShown).ToList();

    private void MarkTouched(DisplayProvider provider)
    {
        if (AppEnvironment.IsDemo) return;
        switch (provider)
        {
            case DisplayProvider.Claude:
                _claudeTouched = true;
                Preferences.Set(ClaudeTouchedKey, true);
                break;
            case DisplayProvider.Codex:
                _codexTouched = true;
                Preferences.Set(CodexTouchedKey, true);
                break;
            default:
                break;
        }
    }

    private void Persist()
    {
        if (AppEnvironment.IsDemo) return;
        Preferences.SetBatch(values =>
        {
            values[OrderKey] = JsonSerializer.SerializeToElement(_order.Select(p => p.RawValue()).ToList());
            values[EnabledKey] = JsonSerializer.SerializeToElement(_enabled.Select(p => p.RawValue()).ToList());
            values[ClaudeKey] = JsonSerializer.SerializeToElement(_enabled.Contains(DisplayProvider.Claude));
            values[CodexKey] = JsonSerializer.SerializeToElement(_enabled.Contains(DisplayProvider.Codex));
        });
    }

    private void RaiseAll()
    {
        var names = new[]
        {
            nameof(Enabled),
            nameof(SelectedCount),
            nameof(SlotProviders),
            nameof(SoloSlotProvider),
            nameof(ClaudeVisible),
            nameof(CodexVisible),
            nameof(ClaudeShown),
            nameof(CodexShown),
            nameof(ClaudePanelShown),
            nameof(CodexPanelShown),
            nameof(AntigravityPanelShown),
            nameof(GrokPanelShown),
            nameof(CursorPanelShown),
            nameof(GuestPanelCount),
        };
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
