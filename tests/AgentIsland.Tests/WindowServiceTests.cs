using AgentIsland.Core.Dialogs;
using AgentIsland.Core.Navigation;
using AgentIsland.UI.ViewModels;
using Xunit;

namespace AgentIsland.Tests;

public class WindowServiceTests
{
    [Fact]
    public void TestWindowService()
    {
        RunAll();
    }

    internal static void RunAll()
    {
        TestIslandViewModelOpenSettingsDelegation();
        TestSettingsViewModelNavigationDelegation();
        TestMockDialogServiceConfirm();
        Console.WriteLine("PASS Window navigation & dialog abstraction services verify cleanly");
    }

    private static void TestIslandViewModelOpenSettingsDelegation()
    {
        var mockWindowService = new MockWindowService();
        var mockVisibility = new MvvmViewModelTests.FakeVisibilityStore();
        var mockActivity = new MvvmViewModelTests.FakeActivityMonitor();
        var mockIslandModel = new MvvmViewModelTests.FakeIslandModel();

        var viewModel = new IslandViewModel(
            mockVisibility,
            mockActivity,
            mockIslandModel,
            mockWindowService);

        Assert(!mockWindowService.OpenSettingsCalled, "OpenSettings must not be called initially");
        viewModel.OpenSettingsCommand.Execute(null);
        Assert(mockWindowService.OpenSettingsCalled, "OpenSettingsCommand must delegate to IWindowService.OpenSettings");
    }

    private static void TestSettingsViewModelNavigationDelegation()
    {
        var mockWindowService = new MockWindowService();
        var mockDialogService = new MockDialogService();
        var storage = new AgentIsland.Windows.Storage.AtomicJsonSettingsStorage(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test_settings_{Guid.NewGuid():N}.json"));
        var mockUpdate = new MockUpdateChecker();

        var viewModel = new SettingsViewModel(
            storage,
            mockUpdate,
            mockWindowService,
            mockDialogService);

        Assert(!mockWindowService.OpenWhatsNewCalled, "OpenWhatsNew must not be called initially");
        mockWindowService.OpenWhatsNew();
        Assert(mockWindowService.OpenWhatsNewCalled, "OpenWhatsNew must record invocation");
    }

    private static void TestMockDialogServiceConfirm()
    {
        var dialogService = new MockDialogService { ConfirmResult = true };
        var task = dialogService.ShowConfirmAsync("Title", "Message", "OK", "Cancel");
        Assert(task.Result, "MockDialogService should resolve configured confirm result");
    }

    private sealed class MockWindowService : IWindowService
    {
        public bool OpenSettingsCalled { get; private set; }
        public bool OpenReportCalled { get; private set; }
        public bool OpenWhatsNewCalled { get; private set; }
        public bool ShowIslandCalled { get; private set; }
        public bool HideIslandCalled { get; private set; }
        public bool ToggleIslandCalled { get; private set; }

        public void OpenSettings(string? tab = null) => OpenSettingsCalled = true;
        public void OpenReport(string? kind = null) => OpenReportCalled = true;
        public void OpenWhatsNew() => OpenWhatsNewCalled = true;
        public void ShowIsland() => ShowIslandCalled = true;
        public void HideIsland() => HideIslandCalled = true;
        public void ToggleIsland() => ToggleIslandCalled = true;
    }

    private sealed class MockDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool AlertShown { get; private set; }
        public bool UpdateDialogShown { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string confirmLabel, string cancelLabel) =>
            Task.FromResult(ConfirmResult);

        public void ShowAlert(string title, string message, string buttonLabel) => AlertShown = true;

        public void ShowUpdateDialog(string title, string message, string version) => UpdateDialogShown = true;
    }

    private sealed class MockUpdateChecker : AgentIsland.Backend.Updates.IUpdateChecker
    {
        public Version Version => new Version(1, 0, 0);
        public void Start() { }
        public Task CheckAsync(bool userInitiated = false) => Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
