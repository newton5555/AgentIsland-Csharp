using System.Windows;
using AgentIsland.Core;
using AgentIsland.Core.Options;
using AgentIsland.Core.Storage;
using AgentIsland.Core.Usage;
using AgentIsland.UI;
using AgentIsland.UI.ViewModels;
using AgentIsland.UI.Providers;

namespace AgentIsland.Tests;

public class MvvmViewModelTests
{
    [WpfFact]
    public void TestAll() => RunAll();

    internal static void RunAll()
    {
        TestIslandViewModel();
        TestUsagePageViewModel();
        TestCostPageViewModel();
        TestSettingsViewModel();
        TestConstructorInjection();
        Console.WriteLine("MvvmViewModelTests GREEN");
    }

    [WpfFact]
    public static void TestIslandViewModel()
    {
        var model = new IslandModel();
        var storage = new MemorySettingsStorage();
        var visibility = new AgentIsland.Backend.Settings.ProviderVisibilityStore(storage);
        var activity = new AgentIsland.Backend.Monitoring.ActivityMonitor();
        using var vm = new IslandViewModel(visibility, activity, model);
        Assert(vm.State == IslandState.Compact || vm.State == IslandState.Expanded, "IslandViewModel should have valid state");

        var initialState = vm.State;
        vm.ToggleExpandCommand.Execute(null);
        var toggledState = vm.State;
        Assert(toggledState != initialState, "ToggleExpandCommand should toggle IslandState");
        Assert(model.State == toggledState, "IslandViewModel should sync with IslandModel.State");

        // Toggle back
        vm.ToggleExpandCommand.Execute(null);
        Assert(vm.State == initialState, "ToggleExpandCommand should revert IslandState");

        Console.WriteLine("PASS IslandViewModel state and toggle binding");
    }

    [WpfFact]
    public static void TestUsagePageViewModel()
    {
        var visibility = new AgentIsland.Backend.Settings.ProviderVisibilityStore(new MemorySettingsStorage());
        var usage = new AgentIsland.Backend.Usage.UsageStore(visibility);
        using var vm = new UsagePageViewModel(visibility, usage);
        Assert(vm.RefreshCommand != null, "RefreshCommand must exist");
        Assert(vm.StartClaudeReauthCommand != null, "StartClaudeReauthCommand must exist");

        // Check slots reflect visibility store
        var slots = visibility.SlotProviders;
        if (slots.Count > 0)
        {
            Assert(vm.LeftSlot != null, "LeftSlot should be populated if slots exist");
            Assert(vm.LeftSlot!.Provider == slots[0], "LeftSlot provider should match Slot0");
        }

        Console.WriteLine("PASS UsagePageViewModel slot projection and commands");
    }

    [WpfFact]
    public static void TestCostPageViewModel()
    {
        var visibility = new AgentIsland.Backend.Settings.ProviderVisibilityStore(new MemorySettingsStorage());
        var cost = new AgentIsland.Backend.Cost.CostStore();
        using var vm = new CostPageViewModel(visibility, cost);
        Assert(vm.RefreshCommand != null, "RefreshCommand must exist");

        var slots = visibility.SlotProviders;
        if (slots.Count > 0)
        {
            Assert(vm.LeftSlot != null, "LeftSlot should be populated if slots exist");
            Assert(vm.LeftSlot!.Provider == slots[0], "LeftSlot provider should match Slot0");
        }

        Console.WriteLine("PASS CostPageViewModel slot projection and commands");
    }

    [WpfFact]
    public static void TestSettingsViewModel()
    {
        var vm = new SettingsViewModel();
        vm.SelectTabCommand.Execute("Display");
        Assert(vm.ActiveTab == "Display", "SelectTabCommand should change ActiveTab to Display");

        vm.SelectTabCommand.Execute("General");
        Assert(vm.ActiveTab == "General", "SelectTabCommand should change ActiveTab to General");

        Assert(vm.IslandScale >= 1.0 && vm.IslandScale <= 1.5, "IslandScale should be clamped between 1.0 and 1.5");

        Console.WriteLine("PASS SettingsViewModel tab selection and property loading");
    }

    [WpfFact]
    public static void TestConstructorInjection()
    {
        var fakeVisibility = new FakeVisibilityStore();
        var fakeActivity = new FakeActivityMonitor();
        var fakeIslandModel = new FakeIslandModel();
        var fakeUsage = new FakeUsageStore();

        // 1. Pure Constructor Injection for IslandViewModel
        using var islandVm = new IslandViewModel(fakeVisibility, fakeActivity, fakeIslandModel);
        Assert(islandVm.LeftProvider == DisplayProvider.Claude, "Injected LeftProvider should be Claude");
        Assert(islandVm.LeftActivity == ActivityState.Working, "Injected LeftActivity should be Working");
        Assert(islandVm.LeftThreadTitle == "Injected Task", "Injected LeftThreadTitle should match");
        Assert(islandVm.State == IslandState.Compact, "Initial state should match fake IslandModel");

        islandVm.ToggleExpandCommand.Execute(null);
        Assert(islandVm.State == IslandState.Expanded, "Toggled state should be Expanded");
        Assert(fakeIslandModel.State == IslandState.Expanded, "Fake IslandModel state should be mutated via DI");

        // 2. Pure Constructor Injection for UsagePageViewModel
        using var usageVm = new UsagePageViewModel(fakeVisibility, fakeUsage);
        Assert(usageVm.LeftSlot != null, "LeftSlot should be populated from fakeVisibility");
        Assert(usageVm.LeftSlot!.Provider == DisplayProvider.Claude, "LeftSlot provider should be Claude");
        Assert(usageVm.LeftSlot!.Usage.FiveHour.UsedPercent == 0.75, "LeftSlot usage should reflect injected FakeUsageStore");

        usageVm.RefreshCommand.Execute(null);
        Assert(fakeUsage.RefreshCalled, "RefreshCommand should invoke Refresh on injected IUsageStore");

        Console.WriteLine("PASS Pure Constructor Injection without singletons or disk dependencies");
    }

    internal sealed class FakeVisibilityStore : AgentIsland.Backend.Settings.IProviderVisibilityStore
    {
        public IReadOnlyList<DisplayProvider> SlotProviders { get; set; } = new[] { DisplayProvider.Claude, DisplayProvider.Codex };
        public IReadOnlyList<DisplayProvider> Slots => SlotProviders;
        public IReadOnlyList<DisplayProvider> Enabled => SlotProviders;
        public IReadOnlyList<DisplayProvider> Order => SlotProviders;
        public bool ClaudeVisible { get; set; } = true;
        public bool CodexVisible { get; set; } = true;
        public bool ClaudeShown => ClaudeVisible;
        public bool CodexShown => CodexVisible;
        public bool ClaudePanelShown => ClaudeVisible;
        public bool CodexPanelShown => CodexVisible;
        public bool AntigravityPanelShown => true;
        public bool GrokPanelShown => false;
        public bool CursorPanelShown => false;
        public bool DeepSeekPanelShown => false;
        public int GuestPanelCount => 0;
        public bool IsVisible(TriggerTool tool) => true;
        public void RedetectGuests() { }
        public int SelectedCount => SlotProviders.Count;
        public bool ClaudeDetected => true;
        public bool CodexDetected => true;
        public bool AntigravityDetected => true;
        public bool GrokDetected => false;
        public bool CursorDetected => false;
        public bool DeepSeekDetected => false;
        public bool SetEnabled(DisplayProvider provider, bool enabled) => true;
        public void MoveProvider(int oldIndex, int newIndex) { }
        public bool IsShown(DisplayProvider provider) => SlotProviders.Contains(provider);
        public bool IsEnabled(DisplayProvider provider) => SlotProviders.Contains(provider);
        public bool Toggle(DisplayProvider provider) => true;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    internal sealed class FakeActivityMonitor : AgentIsland.Backend.Monitoring.IActivityMonitor
    {
        public ActivityState Claude => ActivityState.Working;
        public ActivityState Codex => ActivityState.Idle;
        public ActivityState StateFor(TriggerTool tool) => tool == TriggerTool.Claude ? ActivityState.Working : ActivityState.Idle;
        public AgentIsland.Backend.Monitoring.ActivityMonitor.ActiveThread? ThreadFor(TriggerTool tool) =>
            new AgentIsland.Backend.Monitoring.ActivityMonitor.ActiveThread("s1", "Injected Task", @"C:\p", DateTimeOffset.UtcNow, null, null, SessionLaunchTarget.Cli);
        public void Configure(AgentIsland.Core.Agents.IAgentCatalog catalog) { }
        public void Demo(ActivityState? state) { }
        public void Start() { }
        public void Stop() { }
        public void ScanNow() { }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    internal sealed class FakeIslandModel : IIslandModel
    {
        public IslandState State { get; set; } = IslandState.Compact;
        public AgentIsland.UI.IslandSpacingMode SpacingMode { get; set; } = AgentIsland.UI.IslandSpacingMode.NotchStyle;
        public double ExpandedContentHeight { get; set; } = 188;
        public TriggerTool? SoloProvider => null;
        public double NotchWidth => 200;
        public Size Size => new Size(200, 36);
        public double CornerRadius => 14;
        public void NotifyAlwaysShowUsageChanged() { }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    internal sealed class FakeUsageStore : AgentIsland.Backend.Usage.IUsageStore
    {
        public bool RefreshCalled { get; private set; }
        public AppUsage Claude { get; set; } = new AppUsage(new WindowUsage(0.75, null, null), WindowUsage.Unknown);
        public AppUsage Codex { get; set; } = AppUsage.Empty;
        public AppUsage Usage(DisplayProvider provider) => provider == DisplayProvider.Claude ? Claude : Codex;
        public DateTimeOffset? LastUpdated => DateTimeOffset.UtcNow;
        public string? RefreshWarning => null;
        public bool Loading => false;
        public bool ClaudeReauthInProgress => false;
        public bool CodexReauthInProgress => false;
        public string? ClaudeReauthFailureCaption => null;
        public string? CodexAutoSwitched { get; set; }
        public void Refresh() => RefreshCalled = true;
        public void RefreshIfStale() { }
        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCalled = true;
            return Task.CompletedTask;
        }
        public void ReauthenticateClaude() { }
        public bool ReauthenticateCodex() => true;
        public void ClearClaudeReauthFailure() { }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
