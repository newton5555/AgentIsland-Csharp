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
        Console.WriteLine("MvvmViewModelTests GREEN");
    }

    [WpfFact]
    public static void TestIslandViewModel()
    {
        using var vm = new IslandViewModel();
        Assert(vm.State == IslandState.Compact || vm.State == IslandState.Expanded, "IslandViewModel should have valid state");

        var initialState = vm.State;
        vm.ToggleExpandCommand.Execute(null);
        var toggledState = vm.State;
        Assert(toggledState != initialState, "ToggleExpandCommand should toggle IslandState");
        Assert(IslandModel.Shared.State == toggledState, "IslandViewModel should sync with IslandModel.Shared.State");

        // Toggle back
        vm.ToggleExpandCommand.Execute(null);
        Assert(vm.State == initialState, "ToggleExpandCommand should revert IslandState");

        Console.WriteLine("PASS IslandViewModel state and toggle binding");
    }

    [WpfFact]
    public static void TestUsagePageViewModel()
    {
        using var vm = new UsagePageViewModel();
        Assert(vm.RefreshCommand != null, "RefreshCommand must exist");
        Assert(vm.StartClaudeReauthCommand != null, "StartClaudeReauthCommand must exist");

        // Check slots reflect visibility store
        var slots = AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.SlotProviders;
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
        using var vm = new CostPageViewModel();
        Assert(vm.RefreshCommand != null, "RefreshCommand must exist");

        var slots = AgentIsland.Backend.Settings.ProviderVisibilityStore.Shared.SlotProviders;
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
