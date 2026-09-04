using System;
using System.Collections.Generic;
using System.Linq;
using AgentIsland.Avalonia.ViewModels;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;
using AgentIsland.Runtime.Refresh;
using AgentIsland.Runtime.Snapshots;
using Avalonia.Threading;

namespace AgentIsland.Avalonia;

/// <summary>
/// Disposable unidirectional binding adapter connecting <see cref="AgentRuntime"/> to <see cref="IslandViewModel"/>.
/// Marshals background snapshot updates safely to the Avalonia UI thread without acting as a data source itself.
/// Does NOT initiate or control refresh loops (<see cref="AgentRuntime.RefreshAsync"/> or <see cref="AgentRuntime.RunAsync"/>).
/// </summary>
public class RuntimeIslandBinder : IDisposable
{
    public static readonly IReadOnlyList<string> DefaultProviderOrder = new[]
    {
        "codex",
        "deepseek",
        "antigravity",
        "claude",
        "grok",
        "cursor",
    };

    private readonly IslandViewModel _viewModel;
    private readonly AgentRuntime _runtime;
    private readonly IReadOnlyList<string> _order;
    private readonly Action<Action> _uiDispatcher;
    private readonly object _gate = new();

    private bool _attached;
    private bool _disposed;

    public RuntimeIslandBinder(
        IslandViewModel viewModel,
        AgentRuntime runtime,
        IEnumerable<string>? preferredOrder = null)
        : this(viewModel, runtime, preferredOrder, null)
    {
    }

    internal RuntimeIslandBinder(
        IslandViewModel viewModel,
        AgentRuntime runtime,
        IEnumerable<string>? preferredOrder,
        Action<Action>? uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(runtime);

        _viewModel = viewModel;
        _runtime = runtime;
        _order = SanitizeOrder(preferredOrder);
        _uiDispatcher = uiDispatcher ?? DefaultPostToUI;
    }

    /// <summary>
    /// Target ViewModel receiving slot updates.
    /// </summary>
    public IslandViewModel ViewModel => _viewModel;

    /// <summary>
    /// Source runtime publishing agent snapshots.
    /// </summary>
    public AgentRuntime Runtime => _runtime;

    /// <summary>
    /// Current ordered provider priority.
    /// </summary>
    public IReadOnlyList<string> ProviderOrder => _order;

    /// <summary>
    /// True if currently subscribed to the runtime's snapshot events.
    /// </summary>
    public bool IsAttached
    {
        get
        {
            lock (_gate) return _attached;
        }
    }

    /// <summary>
    /// True if this binder has been disposed.
    /// </summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate) return _disposed;
        }
    }

    /// <summary>
    /// Attaches the single subscription to <see cref="AgentRuntime.SnapshotsChanged"/>.
    /// If in-memory snapshots already exist in <see cref="AgentRuntime.Snapshots"/>, dispatches them
    /// to the UI thread immediately. Does NOT trigger a background refresh.
    /// Idempotent if already attached.
    /// </summary>
    public void Attach()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attached) return;

            _runtime.SnapshotsChanged += OnSnapshotsChanged;
            _attached = true;

            var existing = _runtime.Snapshots;
            if (existing.Count > 0)
            {
                _uiDispatcher(() =>
                {
                    lock (_gate)
                    {
                        if (_disposed || !_attached) return;
                    }
                    UpdateSlots(existing);
                });
            }
        }
    }

    /// <summary>
    /// Detaches the subscription from <see cref="AgentRuntime.SnapshotsChanged"/>.
    /// Subsequent snapshot events will be ignored.
    /// Idempotent if already detached.
    /// </summary>
    public void Detach()
    {
        lock (_gate)
        {
            if (!_attached) return;
            _runtime.SnapshotsChanged -= OnSnapshotsChanged;
            _attached = false;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                if (_attached)
                {
                    _runtime.SnapshotsChanged -= OnSnapshotsChanged;
                    _attached = false;
                }
            }
        }
    }

    private void OnSnapshotsChanged(object? sender, AgentSnapshotsChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !_attached) return;
        }

        var snapshots = e.Snapshots;
        _uiDispatcher(() =>
        {
            lock (_gate)
            {
                if (_disposed || !_attached) return;
            }
            UpdateSlots(snapshots);
        });
    }

    private void UpdateSlots(IReadOnlyDictionary<AgentKey, AgentSnapshot> snapshots)
    {
        var candidates = SelectCandidates(snapshots);
        var capped = candidates.Take(IslandViewModel.MaxSlots).ToList();

        var existingSlots = _viewModel.Slots;
        if (existingSlots.Count == capped.Count && CanReuse(existingSlots, capped))
        {
            for (int i = 0; i < capped.Count; i++)
            {
                existingSlots[i].Name = capped[i].Name;
                existingSlots[i].ShortId = capped[i].ShortId;
                existingSlots[i].ColorKey = capped[i].ColorKey;
                existingSlots[i].Status = capped[i].Status;
                existingSlots[i].StatusText = capped[i].StatusText;
                existingSlots[i].QuotaText = capped[i].QuotaText;
            }
        }
        else
        {
            _viewModel.SetSlots(capped);
        }
    }

    private static bool CanReuse(IList<ProviderSlotViewModel> existing, IList<ProviderSlotViewModel> incoming)
    {
        for (int i = 0; i < existing.Count; i++)
        {
            if (!string.Equals(existing[i].ShortId, incoming[i].ShortId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private List<ProviderSlotViewModel> SelectCandidates(IReadOnlyDictionary<AgentKey, AgentSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return new List<ProviderSlotViewModel>();
        }

        return snapshots.Values
            .Select(s => (Snapshot: s, Rank: GetRank(s.Agent)))
            .OrderBy(x => x.Rank.OrderIndex)
            .ThenBy(x => x.Rank.SecondaryKey, StringComparer.OrdinalIgnoreCase)
            .Take(IslandViewModel.MaxSlots)
            .Select(x => CreateSlotViewModel(x.Snapshot))
            .ToList();
    }

    private (int OrderIndex, string SecondaryKey) GetRank(AgentKey agent)
    {
        var raw = agent.Value.Trim().ToLowerInvariant();
        var canonical = TriggerToolExtensions.FromRawValue(raw)?.RawValue() ?? raw;

        for (int i = 0; i < _order.Count; i++)
        {
            var target = _order[i].Trim().ToLowerInvariant();
            var targetCanonical = TriggerToolExtensions.FromRawValue(target)?.RawValue() ?? target;

            if (string.Equals(raw, target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(canonical, targetCanonical, StringComparison.OrdinalIgnoreCase))
            {
                return (i, raw);
            }
        }

        return (int.MaxValue, raw);
    }

    public static ProviderSlotViewModel CreateSlotViewModel(AgentSnapshot snapshot)
    {
        var (name, shortId, colorKey) = ResolveMetadata(snapshot.Agent);
        var (status, statusText) = ResolveStatus(snapshot);
        var quotaText = ResolveQuota(snapshot);

        return new ProviderSlotViewModel
        {
            Name = name,
            ShortId = shortId,
            ColorKey = colorKey,
            Status = status,
            StatusText = statusText,
            QuotaText = quotaText,
        };
    }

    public static (string Name, string ShortId, string ColorKey) ResolveMetadata(AgentKey agent)
    {
        var raw = agent.Value.Trim().ToLowerInvariant();

        var tool = TriggerToolExtensions.FromRawValue(raw);
        if (tool.HasValue)
        {
            return tool.Value switch
            {
                TriggerTool.Codex => ("Codex", "C", "IslandCodexBrush"),
                TriggerTool.DeepSeek => ("DeepSeek", "D", "IslandDeepSeekBrush"),
                TriggerTool.Antigravity => ("Antigravity", "A", "IslandAntigravityBrush"),
                TriggerTool.Claude => ("Claude", "Cl", "IslandClaudeBrush"),
                TriggerTool.Grok => ("Grok", "G", "IslandGrokBrush"),
                TriggerTool.Cursor => ("Cursor", "Cu", "IslandCursorBrush"),
                _ => (AgentNames.DisplayName(tool.Value), DefaultShortId(agent.Value), "IslandCodexBrush"),
            };
        }

        return raw switch
        {
            "codex" => ("Codex", "C", "IslandCodexBrush"),
            "deepseek" or "dsh" => ("DeepSeek", "D", "IslandDeepSeekBrush"),
            "antigravity" or "gemini" => ("Antigravity", "A", "IslandAntigravityBrush"),
            "claude" => ("Claude", "Cl", "IslandClaudeBrush"),
            "grok" => ("Grok", "G", "IslandGrokBrush"),
            "cursor" => ("Cursor", "Cu", "IslandCursorBrush"),
            _ => (ToTitleCase(agent.Value), DefaultShortId(agent.Value), "IslandCodexBrush"),
        };
    }

    public static (ProviderSlotStatus Status, string StatusText) ResolveStatus(AgentSnapshot snapshot)
    {
        // SnapshotAvailability.Error / Stale cannot be silenced into Ready; display Error or Stale text
        if (snapshot.Availability == SnapshotAvailability.Error)
        {
            return (ProviderSlotStatus.Error, "Error");
        }

        if (snapshot.Availability == SnapshotAvailability.Stale)
        {
            return (ProviderSlotStatus.Error, "Stale");
        }

        // NoData / NotConfigured display clear text under Idle status
        if (snapshot.Availability == SnapshotAvailability.NotConfigured)
        {
            return (ProviderSlotStatus.Idle, "Not Configured");
        }

        if (snapshot.Availability == SnapshotAvailability.NoData)
        {
            return (ProviderSlotStatus.Idle, "No Data");
        }

        // ActivityState mapping
        if (snapshot.Activity == ActivityState.Working)
        {
            return (ProviderSlotStatus.Working, "Working");
        }

        if (snapshot.Activity == ActivityState.NeedsYou)
        {
            return (ProviderSlotStatus.NeedsYou, "Needs You");
        }

        if (snapshot.Activity is ActivityState.Stalled
            or ActivityState.RateLimited
            or ActivityState.AuthRequired)
        {
            return (ProviderSlotStatus.Error, "Error");
        }

        // Default Ready Idle state
        return (ProviderSlotStatus.Idle, "Ready");
    }

    public static string ResolveQuota(AgentSnapshot snapshot)
    {
        // Do NOT substitute snapshot.Cost (TodayTokens, TodayDollars, etc.) as quota/balance
        if (snapshot.Usage is null)
        {
            return string.Empty;
        }

        var usage = snapshot.Usage;
        if (usage.IsErrorOnly || usage == AppUsage.Empty)
        {
            return string.Empty;
        }

        // Check primary window (FiveHour)
        if (IsWindowUsable(usage.FiveHour))
        {
            return FormatPercent(usage.FiveHour.UsedPercent);
        }

        // Fall back to secondary window (Weekly)
        if (IsWindowUsable(usage.Weekly))
        {
            return FormatPercent(usage.Weekly.UsedPercent);
        }

        return string.Empty;
    }

    private static bool IsWindowUsable(WindowUsage? window)
    {
        if (window is null) return false;
        if (window.HasError) return false;
        if (string.Equals(window.Error, "no data", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string FormatPercent(double fraction)
    {
        var pct = fraction <= 1.0
            ? Formatting.PercentInt(fraction)
            : (int)Math.Round(fraction, MidpointRounding.AwayFromZero);
        return $"{pct}%";
    }

    private static string DefaultShortId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "?";
        var clean = raw.Trim();
        return clean.Length <= 2 ? clean.ToUpperInvariant() : clean[..2].ToUpperInvariant();
    }

    private static string ToTitleCase(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        return char.ToUpperInvariant(trimmed[0]) + (trimmed.Length > 1 ? trimmed[1..] : "");
    }

    private static IReadOnlyList<string> SanitizeOrder(IEnumerable<string>? preferredOrder)
    {
        if (preferredOrder is null) return DefaultProviderOrder;

        var list = new List<string>();
        foreach (var item in preferredOrder)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                var clean = item.Trim().ToLowerInvariant();
                if (!list.Contains(clean))
                {
                    list.Add(clean);
                }
            }
        }

        foreach (var defaultItem in DefaultProviderOrder)
        {
            if (!list.Contains(defaultItem))
            {
                list.Add(defaultItem);
            }
        }

        return list;
    }

    private static void DefaultPostToUI(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}

/// <summary>
/// Alias for <see cref="RuntimeIslandBinder"/> following repository controller naming conventions.
/// </summary>
public sealed class RuntimeIslandController : RuntimeIslandBinder
{
    public RuntimeIslandController(
        IslandViewModel viewModel,
        AgentRuntime runtime,
        IEnumerable<string>? preferredOrder = null)
        : base(viewModel, runtime, preferredOrder)
    {
    }
}
