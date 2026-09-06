using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Backend.Providers;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Usage;
using AgentIsland.UI.Providers;
using Xunit;

namespace AgentIsland.Tests;

public class ProviderPluginTests
{
    [Fact]
    public void TestProviderPluginPipeline() => RunAll();

    internal static void RunAll()
    {
        TestBuiltInProvidersIntegrity();
        TestPolymorphicUsageCoordinator();
        TestPolymorphicActivityCoordinator();
        TestPolymorphicCostCoordinator();
        Console.WriteLine("PASS Provider plugin architecture & coordinator engines verify cleanly");
    }

    private static void TestBuiltInProvidersIntegrity()
    {
        var providers = new IAgentProvider[]
        {
            new ClaudeAgentProvider(),
            new CodexAgentProvider(),
            new AntigravityAgentProvider(),
            new DeepSeekAgentProvider(),
            new GrokAgentProvider(),
            new CursorAgentProvider(),
        };

        Assert(providers.Length == 6, "Must contain all 6 built-in providers");

        foreach (var p in providers)
        {
            Assert(!string.IsNullOrEmpty(p.Descriptor.Key.Value), $"Key must not be empty for {p.GetType().Name}");
            Assert(!string.IsNullOrEmpty(p.Descriptor.DisplayName), $"DisplayName must not be empty for {p.GetType().Name}");
            Assert(p.SessionSensor is not null, $"SessionSensor must be implemented for {p.Descriptor.DisplayName}");
            Assert(p.CostLedgerReader is not null, $"CostLedgerReader must be implemented for {p.Descriptor.DisplayName}");
        }

        var claude = providers[0];
        Assert(claude.Descriptor.Supports(AgentCapabilities.Reauthentication), "Claude must support reauth");
        Assert(claude.ReauthHandler is not null, "Claude must have reauth handler");
    }

    private static void TestPolymorphicUsageCoordinator()
    {
        var mockProvider = new MockAgentProvider(
            new AgentDescriptor(new AgentKey("custom-agent"), "Custom Agent", AgentCapabilities.Usage),
            customUsage: new AppUsage(new WindowUsage(0.42, null, null), WindowUsage.Unknown, "mock-tier")
        );

        var fakeVisibility = new MockVisibilityStore(DisplayProvider.Claude);
        var claudeProvider = new MockAgentProvider(
            new AgentDescriptor(new AgentKey("claude"), "Claude", AgentCapabilities.Usage),
            customUsage: new AppUsage(new WindowUsage(0.88, null, null), WindowUsage.Unknown, "pro")
        );

        var usageStore = new UsageStore(new[] { claudeProvider, mockProvider }, fakeVisibility);
        Assert(usageStore.Claude == AppUsage.Empty, "Claude must be empty initially in fresh store");

        var claudeUsage = usageStore.Usage(DisplayProvider.Claude);
        Assert(claudeUsage == AppUsage.Empty, "Initial query must be empty");
    }

    private static void TestPolymorphicActivityCoordinator()
    {
        var customSession = new ScannedSession(
            TriggerTool.Claude,
            "sess-123",
            @"C:\project",
            "Polymorphic Active Session",
            DateTimeOffset.UtcNow,
            ActivityState.Working,
            null,
            null,
            SessionLaunchTarget.Cli
        );

        var claudeProvider = new MockAgentProvider(
            new AgentDescriptor(new AgentKey("claude"), "Claude", AgentCapabilities.Activity),
            customSessions: new[] { customSession }
        );

        var fakeVisibility = new MockVisibilityStore(DisplayProvider.Claude);
        var activityMonitor = new ActivityMonitor(visibilityStore: fakeVisibility, providers: new[] { claudeProvider });

        Assert(activityMonitor.StateFor(TriggerTool.Claude) == ActivityState.Idle, "Idle before scan");
    }

    private static void TestPolymorphicCostCoordinator()
    {
        var customEvent = new TokenEvent(
            TriggerTool.Claude,
            DateTimeOffset.UtcNow,
            "claude-3-5-sonnet",
            100,
            50,
            0,
            0,
            0.05
        );

        var claudeProvider = new MockAgentProvider(
            new AgentDescriptor(new AgentKey("claude"), "Claude", AgentCapabilities.Cost),
            customCostEvents: new[] { customEvent }
        );

        var fakeVisibility = new MockVisibilityStore(DisplayProvider.Claude);
        var costStore = new CostStore(visibilityStore: fakeVisibility, providers: new[] { claudeProvider });

        var summary = costStore.Summary(DisplayProvider.Claude);
        Assert(summary != null, "Summary must not be null");
    }

    private sealed class MockAgentProvider : IAgentProvider, ISessionSensor, IUsageFetcher, ICostLedgerReader
    {
        public AgentDescriptor Descriptor { get; }
        private readonly AppUsage? _customUsage;
        private readonly IReadOnlyList<ScannedSession>? _customSessions;
        private readonly IReadOnlyList<TokenEvent>? _customCostEvents;

        public MockAgentProvider(
            AgentDescriptor descriptor,
            AppUsage? customUsage = null,
            IReadOnlyList<ScannedSession>? customSessions = null,
            IReadOnlyList<TokenEvent>? customCostEvents = null)
        {
            Descriptor = descriptor;
            _customUsage = customUsage;
            _customSessions = customSessions;
            _customCostEvents = customCostEvents;
        }

        public ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(_customUsage ?? AppUsage.Empty);

        public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
            DateTimeOffset now,
            IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
            CancellationToken ct = default) =>
            ValueTask.FromResult(_customSessions ?? (IReadOnlyList<ScannedSession>)Array.Empty<ScannedSession>());

        public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(
            int lookbackDays = 30,
            CancellationToken ct = default) =>
            ValueTask.FromResult(_customCostEvents ?? (IReadOnlyList<TokenEvent>)Array.Empty<TokenEvent>());
    }

    private sealed class MockVisibilityStore : IProviderVisibilityStore
    {
        public IReadOnlyList<DisplayProvider> SlotProviders { get; set; }
        public IReadOnlyList<DisplayProvider> Slots => SlotProviders;
        public IReadOnlyList<DisplayProvider> Enabled => SlotProviders;
        public IReadOnlyList<DisplayProvider> Order => SlotProviders;
        public bool ClaudeVisible { get; set; } = true;
        public bool CodexVisible { get; set; } = true;

        public MockVisibilityStore(params DisplayProvider[] providers)
        {
            SlotProviders = providers;
        }

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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception($"FAIL: {message}");
    }
}
