using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentIsland.Avalonia.ViewModels;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Core.Usage;
using AgentIsland.Runtime.Refresh;
using AgentIsland.Runtime.Snapshots;

namespace AgentIsland.Avalonia;

internal static class RuntimeIslandBinderVerifier
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL: {message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    public static void Run()
    {
        Console.WriteLine("=== Starting RuntimeIslandBinder Verification ===");

        TestConstructorNullChecks();
        TestLifecycleAndSingleSubscription();
        TestNoAutoStart();
        TestOrderingAndCappingMaxSlots();
        TestCustomPreferredOrder();
        TestProviderIdentityAndColors();
        TestStatusMapping();
        TestCostTokensNotFakedAsQuota();
        TestControllerAlias();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("=== ALL VERIFICATIONS PASSED (RuntimeIslandBinder GREEN) ===");
        Console.ResetColor();
    }

    private static void TestConstructorNullChecks()
    {
        var vm = new IslandViewModel();
        var runtime = new AgentRuntime(Array.Empty<IAgentSnapshotSource>());

        try
        {
            _ = new RuntimeIslandBinder(null!, runtime);
            Expect(false, "Null viewModel should throw ArgumentNullException");
        }
        catch (ArgumentNullException)
        {
            // Expected
        }

        try
        {
            _ = new RuntimeIslandBinder(vm, null!);
            Expect(false, "Null runtime should throw ArgumentNullException");
        }
        catch (ArgumentNullException)
        {
            // Expected
        }

        Console.WriteLine("PASS TestConstructorNullChecks");
    }

    private static void TestLifecycleAndSingleSubscription()
    {
        var vm = new IslandViewModel();
        var source = new SimpleSource("codex", ActivityState.Idle);
        var runtime = new AgentRuntime(new[] { source });

        // Use inline dispatcher for synchronous verification
        var binder = new RuntimeIslandBinder(vm, runtime, null, action => action());

        Expect(!binder.IsAttached, "Binder should not be attached initially");
        Expect(!binder.IsDisposed, "Binder should not be disposed initially");

        binder.Attach();
        Expect(binder.IsAttached, "Binder should be attached after Attach()");

        // Second attach should be idempotent (no duplicate events)
        binder.Attach();
        Expect(binder.IsAttached, "Second Attach() is idempotent");

        // Trigger refresh
        var ok = runtime.RefreshAsync().GetAwaiter().GetResult();
        Expect(ok, "Refresh succeeded");
        Expect(vm.SlotCount == 1, "ViewModel updated after snapshot event");
        Expect(vm.Slots[0].Name == "Codex", "Slot 0 is Codex");

        // Detach
        binder.Detach();
        Expect(!binder.IsAttached, "Binder detached");

        // Change source and refresh; ViewModel should NOT update because detached
        source.CurrentActivity = ActivityState.Working;
        runtime.RefreshAsync().GetAwaiter().GetResult();
        Expect(vm.Slots[0].Status == ProviderSlotStatus.Idle, "ViewModel must not update after Detach()");

        // Re-attach; should receive updates again
        binder.Attach();
        Expect(binder.IsAttached, "Re-attached");
        runtime.RefreshAsync().GetAwaiter().GetResult();
        Expect(vm.Slots[0].Status == ProviderSlotStatus.Working, "ViewModel updates after re-attach");

        // Dispose
        binder.Dispose();
        Expect(binder.IsDisposed, "Binder is disposed");
        Expect(!binder.IsAttached, "Binder is detached on dispose");

        // Attempting to attach after dispose should throw ObjectDisposedException
        try
        {
            binder.Attach();
            Expect(false, "Attach() after Dispose() must throw ObjectDisposedException");
        }
        catch (ObjectDisposedException)
        {
            // Expected
        }

        // Additional events after dispose must not update
        source.CurrentActivity = ActivityState.NeedsYou;
        runtime.RefreshAsync().GetAwaiter().GetResult();
        Expect(vm.Slots[0].Status == ProviderSlotStatus.Working, "Events after dispose must not update ViewModel");

        Console.WriteLine("PASS TestLifecycleAndSingleSubscription");
    }

    private static void TestNoAutoStart()
    {
        var vm = new IslandViewModel();
        var source = new SimpleSource("codex", ActivityState.Idle);
        var runtime = new AgentRuntime(new[] { source });

        Expect(runtime.Snapshots.Count == 0, "Runtime has no snapshots before refresh");

        var binder = new RuntimeIslandBinder(vm, runtime, null, action => action());
        binder.Attach();

        Expect(runtime.Snapshots.Count == 0, "Attach() must NOT call RefreshAsync or RunAsync");
        Expect(source.ReadCallCount == 0, "Source must NOT have been read during Attach()");

        Console.WriteLine("PASS TestNoAutoStart");
    }

    private static void TestOrderingAndCappingMaxSlots()
    {
        var vm = new IslandViewModel();
        var cursor = new SimpleSource("cursor", ActivityState.Idle);
        var deepseek = new SimpleSource("deepseek", ActivityState.Working);
        var codex = new SimpleSource("codex", ActivityState.Idle);
        var antigravity = new SimpleSource("antigravity", ActivityState.Idle);

        var runtime = new AgentRuntime(new[] { cursor, deepseek, codex, antigravity });
        var binder = new RuntimeIslandBinder(vm, runtime, null, action => action());
        binder.Attach();

        runtime.RefreshAsync().GetAwaiter().GetResult();

        // Default order: codex, deepseek, antigravity, claude, grok, cursor
        // MaxSlots is 2, so only codex and deepseek should be displayed
        Expect(vm.SlotCount == 2, $"ViewModel must obey MaxSlots=2, had {vm.SlotCount}");
        Expect(vm.Slots[0].Name == "Codex", $"Slot 0 must be Codex, was {vm.Slots[0].Name}");
        Expect(vm.Slots[1].Name == "DeepSeek", $"Slot 1 must be DeepSeek, was {vm.Slots[1].Name}");

        // Test empty snapshots -> slot count 0
        var emptyRuntime = new AgentRuntime(Array.Empty<IAgentSnapshotSource>());
        var emptyBinder = new RuntimeIslandBinder(vm, emptyRuntime, null, action => action());
        emptyBinder.Attach();
        emptyRuntime.RefreshAsync().GetAwaiter().GetResult();
        Expect(vm.SlotCount == 0, "Empty runtime yields 0 slots");
        Expect(vm.IsEmpty, "ViewModel IsEmpty is true");

        // Test 1 snapshot -> slot count 1
        var singleRuntime = new AgentRuntime(new[] { new SimpleSource("claude", ActivityState.Idle) });
        var singleBinder = new RuntimeIslandBinder(vm, singleRuntime, null, action => action());
        singleBinder.Attach();
        singleRuntime.RefreshAsync().GetAwaiter().GetResult();
        Expect(vm.SlotCount == 1, "Single provider yields 1 slot");
        Expect(vm.Slots[0].Name == "Claude", "Slot 0 is Claude");

        Console.WriteLine("PASS TestOrderingAndCappingMaxSlots");
    }

    private static void TestCustomPreferredOrder()
    {
        var vm = new IslandViewModel();
        var codex = new SimpleSource("codex", ActivityState.Idle);
        var cursor = new SimpleSource("cursor", ActivityState.Idle);
        var claude = new SimpleSource("claude", ActivityState.Idle);

        var runtime = new AgentRuntime(new[] { codex, cursor, claude });
        var customOrder = new[] { "claude", "cursor" };

        var binder = new RuntimeIslandBinder(vm, runtime, customOrder, action => action());
        binder.Attach();

        runtime.RefreshAsync().GetAwaiter().GetResult();

        Expect(vm.SlotCount == 2, "MaxSlots=2 respected");
        Expect(vm.Slots[0].Name == "Claude", "Custom order slot 0 is Claude");
        Expect(vm.Slots[1].Name == "Cursor", "Custom order slot 1 is Cursor");

        Console.WriteLine("PASS TestCustomPreferredOrder");
    }

    private static void TestProviderIdentityAndColors()
    {
        var providers = new (string Key, string ExpectedName, string ExpectedShortId, string ExpectedColor)[]
        {
            ("codex", "Codex", "C", "IslandCodexBrush"),
            ("deepseek", "DeepSeek", "D", "IslandDeepSeekBrush"),
            ("dsh", "DeepSeek", "D", "IslandDeepSeekBrush"),
            ("antigravity", "Antigravity", "A", "IslandAntigravityBrush"),
            ("gemini", "Antigravity", "A", "IslandAntigravityBrush"),
            ("claude", "Claude", "Cl", "IslandClaudeBrush"),
            ("grok", "Grok", "G", "IslandGrokBrush"),
            ("cursor", "Cursor", "Cu", "IslandCursorBrush"),
        };

        foreach (var p in providers)
        {
            var (name, shortId, color) = RuntimeIslandBinder.ResolveMetadata(p.Key);
            Expect(name == p.ExpectedName, $"Provider {p.Key} Name was {name}, expected {p.ExpectedName}");
            Expect(shortId == p.ExpectedShortId, $"Provider {p.Key} ShortId was {shortId}, expected {p.ExpectedShortId}");
            Expect(color == p.ExpectedColor, $"Provider {p.Key} Color was {color}, expected {p.ExpectedColor}");
        }

        Console.WriteLine("PASS TestProviderIdentityAndColors");
    }

    private static void TestStatusMapping()
    {
        var now = DateTimeOffset.Now;

        // 1. ActivityState.Working -> Working
        var working = new AgentSnapshot("codex", ActivityState.Working, SnapshotAvailability.Ready, now);
        var (s1, t1) = RuntimeIslandBinder.ResolveStatus(working);
        Expect(s1 == ProviderSlotStatus.Working && t1 == "Working", "Working maps to Working");

        // 2. ActivityState.NeedsYou -> NeedsYou
        var needsYou = new AgentSnapshot("codex", ActivityState.NeedsYou, SnapshotAvailability.Ready, now);
        var (s2, t2) = RuntimeIslandBinder.ResolveStatus(needsYou);
        Expect(s2 == ProviderSlotStatus.NeedsYou && t2 == "Needs You", "NeedsYou maps to NeedsYou");

        // 3. ActivityState.Stalled / RateLimited / AuthRequired -> Error
        var stalled = new AgentSnapshot("codex", ActivityState.Stalled, SnapshotAvailability.Ready, now);
        var (s3, t3) = RuntimeIslandBinder.ResolveStatus(stalled);
        Expect(s3 == ProviderSlotStatus.Error && t3 == "Error", "Stalled maps to Error");

        var rateLimited = new AgentSnapshot("codex", ActivityState.RateLimited, SnapshotAvailability.Ready, now);
        var (s4, t4) = RuntimeIslandBinder.ResolveStatus(rateLimited);
        Expect(s4 == ProviderSlotStatus.Error && t4 == "Error", "RateLimited maps to Error");

        var authRequired = new AgentSnapshot("codex", ActivityState.AuthRequired, SnapshotAvailability.Ready, now);
        var (s5, t5) = RuntimeIslandBinder.ResolveStatus(authRequired);
        Expect(s5 == ProviderSlotStatus.Error && t5 == "Error", "AuthRequired maps to Error");

        // 4. SnapshotAvailability.Error / Stale must not be silent Ready
        var error = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Error, now);
        var (s6, t6) = RuntimeIslandBinder.ResolveStatus(error);
        Expect(s6 == ProviderSlotStatus.Error && t6 == "Error", "SnapshotAvailability.Error maps to Error");

        var stale = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Stale, now);
        var (s7, t7) = RuntimeIslandBinder.ResolveStatus(stale);
        Expect(s7 == ProviderSlotStatus.Error && t7 == "Stale", "SnapshotAvailability.Stale maps to Stale");

        // 5. NoData / NotConfigured -> Idle with clear text
        var noData = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.NoData, now);
        var (s8, t8) = RuntimeIslandBinder.ResolveStatus(noData);
        Expect(s8 == ProviderSlotStatus.Idle && t8 == "No Data", "NoData maps to Idle / No Data");

        var notConfigured = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.NotConfigured, now);
        var (s9, t9) = RuntimeIslandBinder.ResolveStatus(notConfigured);
        Expect(s9 == ProviderSlotStatus.Idle && t9 == "Not Configured", "NotConfigured maps to Idle / Not Configured");

        // 6. Ready + Idle -> Ready
        var readyIdle = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, now);
        var (s10, t10) = RuntimeIslandBinder.ResolveStatus(readyIdle);
        Expect(s10 == ProviderSlotStatus.Idle && t10 == "Ready", "Ready + Idle maps to Ready");

        Console.WriteLine("PASS TestStatusMapping");
    }

    private static void TestCostTokensNotFakedAsQuota()
    {
        var now = DateTimeOffset.Now;

        // Snapshot with Cost summary but Usage is null -> QuotaText MUST be empty string
        var costSummary = new ProviderCostSummary(
            12.50, 1500, 1500, 50.0, 5000, 5000,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var costOnly = new AgentSnapshot("deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Usage: null, Cost: costSummary);

        var quotaCostOnly = RuntimeIslandBinder.ResolveQuota(costOnly);
        Expect(string.IsNullOrEmpty(quotaCostOnly), $"Cost must NOT be faked as quota/balance, was '{quotaCostOnly}'");

        // Snapshot with AppUsage.Empty -> QuotaText MUST be empty string
        var emptyUsage = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Usage: AppUsage.Empty, Cost: costSummary);
        var quotaEmpty = RuntimeIslandBinder.ResolveQuota(emptyUsage);
        Expect(string.IsNullOrEmpty(quotaEmpty), "AppUsage.Empty must produce empty quota text");

        // Snapshot with IsErrorOnly -> QuotaText MUST be empty string
        var errorUsage = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Usage: AppUsage.ErrorPair("rate limit fail"), Cost: costSummary);
        var quotaError = RuntimeIslandBinder.ResolveQuota(errorUsage);
        Expect(string.IsNullOrEmpty(quotaError), "Error usage must produce empty quota text");

        // Snapshot with explicit valid Usage -> QuotaText displays percent
        var validUsage = new AppUsage(
            new WindowUsage(0.85, now.AddHours(2), null, 18000),
            new WindowUsage(0.30, now.AddDays(5), null, 604800));
        var usableSnapshot = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Usage: validUsage);
        var quotaValid = RuntimeIslandBinder.ResolveQuota(usableSnapshot);
        Expect(quotaValid == "85%", $"Valid usage must produce formatted percent, was '{quotaValid}'");

        Console.WriteLine("PASS TestCostTokensNotFakedAsQuota");
    }

    private static void TestControllerAlias()
    {
        var vm = new IslandViewModel();
        var runtime = new AgentRuntime(Array.Empty<IAgentSnapshotSource>());
        using var controller = new RuntimeIslandController(vm, runtime);

        Expect(controller is RuntimeIslandBinder, "RuntimeIslandController inherits RuntimeIslandBinder");
        controller.Attach();
        Expect(controller.IsAttached, "RuntimeIslandController attaches correctly");
        controller.Detach();
        Expect(!controller.IsAttached, "RuntimeIslandController detaches correctly");

        Console.WriteLine("PASS TestControllerAlias");
    }

    private sealed class SimpleSource : IAgentSnapshotSource
    {
        public SimpleSource(string agent, ActivityState activity)
        {
            Agent = agent;
            CurrentActivity = activity;
        }

        public AgentKey Agent { get; }
        public ActivityState CurrentActivity { get; set; }
        public int ReadCallCount { get; private set; }

        public Task<AgentSnapshot> ReadAsync(DateTimeOffset observedAt, CancellationToken cancellationToken)
        {
            ReadCallCount++;
            return Task.FromResult(new AgentSnapshot(
                Agent,
                CurrentActivity,
                SnapshotAvailability.Ready,
                observedAt,
                observedAt));
        }
    }
}
