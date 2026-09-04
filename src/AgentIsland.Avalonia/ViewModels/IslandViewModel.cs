using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace AgentIsland.Avalonia.ViewModels;

/// <summary>
/// Top-level ViewModel for the floating island view.
/// Supports 0, 1, or 2 provider slots (MaxSlots = 2).
/// Pure visual state model: does not read credentials, process lists, or local logs.
/// Serves as the single binding entry point for future P1 Runtime integration.
/// </summary>
public class IslandViewModel : ViewModelBase
{
    public const int MaxSlots = 2;

    private readonly ObservableCollection<ProviderSlotViewModel> _slots = new();

    public IslandViewModel()
    {
        _slots.CollectionChanged += OnSlotsCollectionChanged;
    }

    /// <summary>
    /// Active provider slots (0 to 2 slots).
    /// </summary>
    public ObservableCollection<ProviderSlotViewModel> Slots => _slots;

    /// <summary>
    /// Current number of slots.
    /// </summary>
    public int SlotCount => _slots.Count;

    /// <summary>
    /// True if there is at least one active slot.
    /// </summary>
    public bool HasSlots => _slots.Count > 0;

    /// <summary>
    /// True if there are zero slots.
    /// </summary>
    public bool IsEmpty => _slots.Count == 0;

    /// <summary>
    /// Attempts to add a slot up to MaxSlots.
    /// Returns false if already at capacity.
    /// </summary>
    public bool TryAddSlot(ProviderSlotViewModel slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (_slots.Count >= MaxSlots)
        {
            return false;
        }

        _slots.Add(slot);
        return true;
    }

    /// <summary>
    /// Replaces the current slots with the provided collection, capped at MaxSlots.
    /// </summary>
    public void SetSlots(IEnumerable<ProviderSlotViewModel> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        _slots.Clear();
        foreach (var slot in slots.Take(MaxSlots))
        {
            _slots.Add(slot);
        }
    }

    /// <summary>
    /// Clears all slots.
    /// </summary>
    public void ClearSlots()
    {
        _slots.Clear();
    }

    private void OnSlotsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SlotCount));
        OnPropertyChanged(nameof(HasSlots));
        OnPropertyChanged(nameof(IsEmpty));
    }

    #region Mock / Demo Factories

    /// <summary>
    /// Design-time singleton accessor returning the standard mock.
    /// </summary>
    public static IslandViewModel Mock => CreateMock();

    /// <summary>
    /// Creates a mock IslandViewModel with 1 slot (Codex, Ready, 85%),
    /// matching the P0 visual baseline. Does NOT touch credentials or files.
    /// </summary>
    public static IslandViewModel CreateMock()
    {
        var vm = new IslandViewModel();
        vm.TryAddSlot(new ProviderSlotViewModel
        {
            Name = "Codex",
            ShortId = "C",
            ColorKey = "IslandCodexBrush",
            Status = ProviderSlotStatus.Idle,
            QuotaText = "85%",
            StatusText = "Ready"
        });
        return vm;
    }

    /// <summary>
    /// Creates a mock IslandViewModel with 2 slots (Codex Idle and DeepSeek Working),
    /// useful for testing multi-slot layouts without real agents.
    /// </summary>
    public static IslandViewModel CreateTwoSlotMock()
    {
        var vm = new IslandViewModel();
        vm.TryAddSlot(new ProviderSlotViewModel
        {
            Name = "Codex",
            ShortId = "C",
            ColorKey = "IslandCodexBrush",
            Status = ProviderSlotStatus.Idle,
            QuotaText = "85%",
            StatusText = "Ready"
        });
        vm.TryAddSlot(new ProviderSlotViewModel
        {
            Name = "DeepSeek",
            ShortId = "D",
            ColorKey = "IslandDeepSeekBrush",
            Status = ProviderSlotStatus.Working,
            QuotaText = "$12.50",
            StatusText = "Working"
        });
        return vm;
    }

    /// <summary>
    /// Creates a mock IslandViewModel with 0 slots, useful for verifying empty state.
    /// </summary>
    public static IslandViewModel CreateEmptyMock()
    {
        return new IslandViewModel();
    }

    #endregion
}
