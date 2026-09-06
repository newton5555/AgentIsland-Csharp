using AgentIsland.Backend.Alarms;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Updates;
using AgentIsland.Backend.Usage;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Dialogs;
using AgentIsland.Core.Navigation;
using AgentIsland.Core.Options;
using AgentIsland.Core.Storage;
using AgentIsland.Core.Threading;
using AgentIsland.Core.Usage;
using AgentIsland.Providers.BuiltIn;
using AgentIsland.UI;
using AgentIsland.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentIsland.Tests;

public class ContainerVerificationTests
{
    private static IServiceProvider CreateTestServiceProvider()
    {
        var services = new ServiceCollection();

        // Infrastructure
        services.AddSingleton<ISettingsStorage, MemorySettingsStorage>();
        services.AddSingleton<SettingsManager>();
        services.AddSingleton<ISettingsManager>(sp => sp.GetRequiredService<SettingsManager>());
        services.AddSingleton<IUiDispatcher, DummyUiDispatcher>();
        services.AddSingleton<IAgentCatalog, BuiltInAgentCatalog>();

        // Domain & Setting Stores
        services.AddSingleton<IslandModel>();
        services.AddSingleton<IIslandModel>(sp => sp.GetRequiredService<IslandModel>());
        services.AddSingleton<IslandScaleStore>();
        services.AddSingleton<LowPowerModeStore>();
        services.AddSingleton<GlowColorStore>();
        services.AddSingleton<AlertThresholdStore>();
        services.AddSingleton<RefreshIntervalStore>();
        services.AddSingleton<AgentReminderStore>();
        services.AddSingleton<QuotaAlarmStore>();

        services.AddSingleton<ProviderVisibilityStore>();
        services.AddSingleton<IProviderVisibilityStore>(sp => sp.GetRequiredService<ProviderVisibilityStore>());

        services.AddSingleton<GrokUsageStore>();
        services.AddSingleton<IGrokUsageStore>(sp => sp.GetRequiredService<GrokUsageStore>());

        services.AddSingleton<CursorUsageStore>();
        services.AddSingleton<ICursorUsageStore>(sp => sp.GetRequiredService<CursorUsageStore>());

        services.AddSingleton<AntigravityUsageStore>();
        services.AddSingleton<IAntigravityUsageStore>(sp => sp.GetRequiredService<AntigravityUsageStore>());

        services.AddSingleton<DeepSeekBalanceStore>();
        services.AddSingleton<IDeepSeekBalanceStore>(sp => sp.GetRequiredService<DeepSeekBalanceStore>());

        services.AddSingleton<CostQueryService>();
        services.AddSingleton<ICostQueryService>(sp => sp.GetRequiredService<CostQueryService>());

        services.AddSingleton<CostStore>();
        services.AddSingleton<ICostStore>(sp => sp.GetRequiredService<CostStore>());

        services.AddSingleton<UsageStore>();
        services.AddSingleton<IUsageStore>(sp => sp.GetRequiredService<UsageStore>());

        services.AddSingleton<ActivityMonitor>();
        services.AddSingleton<IActivityMonitor>(sp => sp.GetRequiredService<ActivityMonitor>());

        services.AddSingleton<TurnAlarmWindowController>(sp => new TurnAlarmWindowController(
            sp.GetService<AgentReminderStore>(),
            sp));
        services.AddSingleton<ITurnAlarmWindowController>(sp => sp.GetRequiredService<TurnAlarmWindowController>());

        services.AddSingleton<AgentReminderCenter>();
        services.AddSingleton<IAgentReminderCenter>(sp => sp.GetRequiredService<AgentReminderCenter>());

        services.AddSingleton<UsageExhaustionAlarm>();
        services.AddSingleton<IUsageExhaustionAlarm>(sp => sp.GetRequiredService<UsageExhaustionAlarm>());

        services.AddSingleton<AlertEngine>();
        services.AddSingleton<IAlertEngine>(sp => sp.GetRequiredService<AlertEngine>());

        services.AddSingleton<UpdateChecker>();
        services.AddSingleton<IUpdateChecker>(sp => sp.GetRequiredService<UpdateChecker>());

        // UI Window and Page stores
        services.AddSingleton<ScreenPref>();
        services.AddSingleton<IslandPositionStore>();
        services.AddSingleton<IslandTargetDisplayStore>();
        services.AddSingleton<QuotaDisplayModeStore>();
        services.AddSingleton<AlwaysShowUsageStore>();
        services.AddSingleton<IWindowService, DummyWindowService>();

        // Windows & ViewModels
        services.AddTransient<IslandWindow>();
        services.AddTransient<IslandViewModel>();
        services.AddTransient<UsagePageViewModel>();
        services.AddTransient<CostPageViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TestAllContractsResolveAsSingletons()
    {
        var sp = CreateTestServiceProvider();

        AssertSingleton<IProviderVisibilityStore>(sp);
        AssertSingleton<IUsageStore>(sp);
        AssertSingleton<ICostStore>(sp);
        AssertSingleton<IActivityMonitor>(sp);
        AssertSingleton<IIslandModel>(sp);
        AssertSingleton<IAgentReminderCenter>(sp);
        AssertSingleton<ITurnAlarmWindowController>(sp);
        AssertSingleton<IUsageExhaustionAlarm>(sp);
        AssertSingleton<IAlertEngine>(sp);
        AssertSingleton<IUpdateChecker>(sp);
        AssertSingleton<IGrokUsageStore>(sp);
        AssertSingleton<ICursorUsageStore>(sp);
        AssertSingleton<IAntigravityUsageStore>(sp);
        AssertSingleton<IDeepSeekBalanceStore>(sp);
    }

    [Fact]
    public void TestViewModelsResolveTransientlyWithInjectedDependencies()
    {
        var sp = CreateTestServiceProvider();

        var island1 = sp.GetRequiredService<IslandViewModel>();
        var island2 = sp.GetRequiredService<IslandViewModel>();
        Xunit.Assert.NotNull(island1);
        Xunit.Assert.NotNull(island2);
        Xunit.Assert.False(ReferenceEquals(island1, island2), "ViewModels must be transient");

        var usage1 = sp.GetRequiredService<UsagePageViewModel>();
        var usage2 = sp.GetRequiredService<UsagePageViewModel>();
        Xunit.Assert.NotNull(usage1);
        Xunit.Assert.NotNull(usage2);
        Xunit.Assert.False(ReferenceEquals(usage1, usage2), "UsagePageViewModel must be transient");

        var cost1 = sp.GetRequiredService<CostPageViewModel>();
        var cost2 = sp.GetRequiredService<CostPageViewModel>();
        Xunit.Assert.NotNull(cost1);
        Xunit.Assert.NotNull(cost2);
        Xunit.Assert.False(ReferenceEquals(cost1, cost2), "CostPageViewModel must be transient");

        var settings1 = sp.GetRequiredService<SettingsViewModel>();
        var settings2 = sp.GetRequiredService<SettingsViewModel>();
        Xunit.Assert.NotNull(settings1);
        Xunit.Assert.NotNull(settings2);
        Xunit.Assert.False(ReferenceEquals(settings1, settings2), "SettingsViewModel must be transient");
    }

    [WpfFact]
    public void TestIslandWindowResolvesFromContainer()
    {
        WpfTestEnvironment.EnsureInitialized();
        var sp = CreateTestServiceProvider();
        var window = sp.GetRequiredService<IslandWindow>();
        Xunit.Assert.NotNull(window);
        Xunit.Assert.NotNull(window.ViewModel);
        window.Close();
    }

    private static void AssertSingleton<T>(IServiceProvider sp) where T : class
    {
        var first = sp.GetRequiredService<T>();
        var second = sp.GetRequiredService<T>();
        Xunit.Assert.NotNull(first);
        Xunit.Assert.Same(first, second);
    }

    private sealed class DummyUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
        public void BeginInvoke(Action action) => action();
    }

    private sealed class DummyWindowService : IWindowService
    {
        public void OpenSettings(string? tab = null) { }
        public void OpenReport(string? kind = null) { }
        public void OpenWhatsNew() { }
        public void ShowIsland() { }
        public void HideIsland() { }
        public void ToggleIsland() { }
    }
}
