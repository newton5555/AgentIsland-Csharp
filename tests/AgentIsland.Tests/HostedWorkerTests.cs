using System.ComponentModel;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Backend.Updates;
using AgentIsland.Backend.Usage;
using AgentIsland.Backend.Workers;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.UI.Providers;
using Xunit;

namespace AgentIsland.Tests;

public class HostedWorkerTests
{
    [Fact]
    public void TestHostedWorkers()
    {
        RunAll();
    }

    internal static void RunAll()
    {
        TestActivityMonitoringWorkerLifecycle().GetAwaiter().GetResult();
        TestUsagePollingWorkerLifecycle().GetAwaiter().GetResult();
        TestCostAggregationWorkerLifecycle().GetAwaiter().GetResult();
        TestUpdateCheckWorkerLifecycle().GetAwaiter().GetResult();
        Console.WriteLine("PASS Generic Host background workers verify cleanly");
    }

    private static async Task TestActivityMonitoringWorkerLifecycle()
    {
        var fakeMonitor = new MockActivityMonitor();
        var worker = new ActivityMonitoringWorker(fakeMonitor);

        using var cts = new CancellationTokenSource();
        var startTask = worker.StartAsync(cts.Token);
        await startTask;

        Assert(fakeMonitor.StartCalled, "ActivityMonitoringWorker must call Start() on startup");


        // Request stop
        await worker.StopAsync(CancellationToken.None);
        Assert(fakeMonitor.StopCalled, "ActivityMonitoringWorker must call Stop() on shutdown");
    }

    private static async Task TestUsagePollingWorkerLifecycle()
    {
        var fakeUsage = new MockUsageStore();
        var worker = new UsagePollingWorker(fakeUsage);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        await worker.StopAsync(CancellationToken.None);
    }

    private static async Task TestCostAggregationWorkerLifecycle()
    {
        var fakeCost = new MockCostStore();
        var worker = new CostAggregationWorker(fakeCost);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        await worker.StopAsync(CancellationToken.None);
    }

    private static async Task TestUpdateCheckWorkerLifecycle()
    {
        var fakeUpdate = new MockUpdateChecker();
        var worker = new UpdateCheckWorker(fakeUpdate);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        await worker.StopAsync(CancellationToken.None);
    }

    private sealed class MockActivityMonitor : IActivityMonitor
    {
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public bool ScanNowCalled { get; private set; }

        public ActivityState StateFor(TriggerTool tool) => ActivityState.Idle;
        public ActivityMonitor.ActiveThread? ThreadFor(TriggerTool tool) => null;
        public void Configure(IAgentCatalog catalog) { }
        public void Demo(ActivityState? state) { }
        public void Start() => StartCalled = true;
        public void Stop() => StopCalled = true;
        public void ScanNow() => ScanNowCalled = true;
        public event PropertyChangedEventHandler? PropertyChanged;
    }


    private sealed class MockUsageStore : IUsageStore
    {
        public int RefreshCallCount { get; private set; }
        public AppUsage Claude => AppUsage.Empty;
        public AppUsage Codex => AppUsage.Empty;
        public AppUsage Usage(DisplayProvider provider) => AppUsage.Empty;
        public DateTimeOffset? LastUpdated => DateTimeOffset.UtcNow;
        public string? RefreshWarning => null;
        public bool Loading => false;
        public bool ClaudeReauthInProgress => false;
        public bool CodexReauthInProgress => false;
        public string? ClaudeReauthFailureCaption => null;
        public string? CodexAutoSwitched { get; set; }
        public void Refresh() => RefreshCallCount++;
        public void RefreshIfStale() { }
        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCallCount++;
            return Task.CompletedTask;
        }
        public void ClearClaudeReauthFailure() { }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class MockCostStore : ICostStore
    {
        public int RefreshCallCount { get; private set; }
        public ProviderCostSummary Summary(DisplayProvider provider) => ProviderCostSummary.Empty;
        public ProviderCostSummary Claude => ProviderCostSummary.Empty;
        public ProviderCostSummary Codex => ProviderCostSummary.Empty;
        public ProviderCostSummary DeepSeek => ProviderCostSummary.Empty;
        public DateTimeOffset? LastUpdated => DateTimeOffset.UtcNow;
        public void Refresh() => RefreshCallCount++;
        public void StartAutoRefresh() { }
        public void StopAutoRefresh() { }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class MockUpdateChecker : IUpdateChecker
    {
        public int CheckCallCount { get; private set; }
        public Version Version => new Version(1, 0, 0);
        public void Start() { }
        public Task CheckAsync(bool userInitiated = false)
        {
            CheckCallCount++;
            return Task.CompletedTask;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
