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
using AgentIsland.Runtime.Sources;

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
        TestLiveConstructionContract();
        TestDeepSeekMergedSlotWorkingAndCost();
        TestFormatBalanceAmountAndCurrencies();
        TestResolveBalanceSingleAndMultiEntry();
        TestSlotBalanceIsolationFromQuotaAndCost();

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

        // Distinct Cost resolution: CostText must format dollars or tokens, and not contaminate Quota
        var costText = RuntimeIslandBinder.ResolveCost(costOnly);
        Expect(costText == "$12.50", $"ResolveCost should format dollar amount, was '{costText}'");
        var slot = RuntimeIslandBinder.CreateSlotViewModel(costOnly);
        Expect(slot.CostText == "$12.50", "SlotViewModel CostText should be set");
        Expect(slot.HasCostText, "HasCostText must be true");
        Expect(!slot.HasQuotaText, "HasQuotaText must remain false when Usage is null");
        Expect(string.IsNullOrEmpty(slot.QuotaText), "QuotaText must remain empty");

        var noCostText = RuntimeIslandBinder.ResolveCost(usableSnapshot);
        Expect(string.IsNullOrEmpty(noCostText), "ResolveCost should be empty when snapshot.Cost is null");

        var tokenCostSummary = new ProviderCostSummary(
            0, 1500, 1500, 0, 1500, 1500,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());
        var tokenOnly = new AgentSnapshot("codex", ActivityState.Idle, SnapshotAvailability.Ready, now, now, Cost: tokenCostSummary);
        var tokenFormatted = RuntimeIslandBinder.ResolveCost(tokenOnly);
        Expect(tokenFormatted == "1.5k tok", $"Token count formatted, was '{tokenFormatted}'");

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

    private static void TestLiveConstructionContract()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"AgentIsland_LiveVerify_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            // Provide isolated empty file providers so verification contract never reads real account/session directories
            var sources = LocalTokenSources.CreateSources(
                cacheDir: tempDir,
                codexFileProvider: Array.Empty<string>,
                deepSeekFileProvider: Array.Empty<string>);
            Expect(sources.Count == 3, "LocalTokenSources creates Codex cost, DeepSeek cost, and DeepSeek activity sources");
            Expect(sources[0].Agent == (AgentKey)"codex", "First source is codex");
            Expect(sources[1].Agent == (AgentKey)"deepseek", "Second source is deepseek cost");
            Expect(sources[2].Agent == (AgentKey)"deepseek", "Third source is deepseek activity");

            var runtime = new AgentRuntime(sources);
            var vm = new IslandViewModel();
            using var binder = new RuntimeIslandBinder(vm, runtime);
            binder.Attach();
            Expect(binder.IsAttached, "Binder is attached");

            using var cts = new CancellationTokenSource();
            var refreshTask = runtime.RefreshAsync(cancellationToken: cts.Token);
            refreshTask.GetAwaiter().GetResult();

            Expect(runtime.Snapshots.Count == 2, "Snapshots populated for both providers (deepseek merged)");
            Expect(vm.Slots.Count <= 2, "ViewModel slots respect capacity");

            cts.Cancel();
            binder.Dispose();
            Expect(!binder.IsAttached, "Binder is detached after dispose");

            Console.WriteLine("PASS TestLiveConstructionContract");
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestDeepSeekMergedSlotWorkingAndCost()
    {
        var now = DateTimeOffset.Now;
        var costSummary = new ProviderCostSummary(
            12.5, 1000, 1000, 25.0, 2000, 2000,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var mergedSnapshot = new AgentSnapshot(
            (AgentKey)"deepseek",
            ActivityState.Working,
            SnapshotAvailability.Ready,
            now,
            now,
            Cost: costSummary);

        var (status, statusText) = RuntimeIslandBinder.ResolveStatus(mergedSnapshot);
        Expect(status == ProviderSlotStatus.Working && statusText == "Working", "DeepSeek resolves to Working");

        var costText = RuntimeIslandBinder.ResolveCost(mergedSnapshot);
        Expect(costText == "$12.50", "CostText resolves correctly");

        var slot = RuntimeIslandBinder.CreateSlotViewModel(mergedSnapshot);
        Expect(slot.Status == ProviderSlotStatus.Working, "Slot status is Working");
        Expect(slot.CostText == "$12.50", "Slot cost text is preserved");
        Expect(slot.HasCostText, "HasCostText is true");

        Console.WriteLine("PASS TestDeepSeekMergedSlotWorkingAndCost");
    }

    private static void TestFormatBalanceAmountAndCurrencies()
    {
        Expect(RuntimeIslandBinder.FormatBalanceAmount("CNY", 12.50m) == "¥12.50", "CNY formats with ¥");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("RMB", 12.50m) == "¥12.50", "RMB formats with ¥");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("USD", 5.00m) == "$5.00", "USD formats with $");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("EUR", 9.99m) == "€9.99", "EUR formats with €");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("JPY", 1000m) == "¥1,000.00", "JPY formats with ¥");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("GBP", 15.20m) == "£15.20", "GBP formats with £");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("CAD", 10.00m) == "CAD 10.00", "Unrecognized currency formats with code prefix");

        // Zero balance
        Expect(RuntimeIslandBinder.FormatBalanceAmount("CNY", 0.00m) == "¥0.00", "Zero balance formats with ¥0.00");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("USD", 0.00m) == "$0.00", "Zero USD formats with $0.00");

        // Negative balance (arrears)
        Expect(RuntimeIslandBinder.FormatBalanceAmount("CNY", -3.20m) == "-¥3.20", "Negative CNY formats with -¥3.20");
        Expect(RuntimeIslandBinder.FormatBalanceAmount("USD", -10.50m) == "-$10.50", "Negative USD formats with -$10.50");

        Console.WriteLine("PASS TestFormatBalanceAmountAndCurrencies");
    }

    private static void TestResolveBalanceSingleAndMultiEntry()
    {
        var now = DateTimeOffset.Now;

        // Null balance
        var snapNull = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now);
        Expect(RuntimeIslandBinder.ResolveBalance(snapNull) == string.Empty, "Null balance resolves to empty string");

        // Empty entries
        var snapEmpty = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Balance: new AccountBalanceSnapshot(true, Array.Empty<AccountBalanceEntry>()));
        Expect(RuntimeIslandBinder.ResolveBalance(snapEmpty) == string.Empty, "Empty entries resolve to empty string");

        // Single entry
        var snapSingle = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Balance: new AccountBalanceSnapshot(true, new[] { new AccountBalanceEntry("CNY", 12.50m, 0m, 12.50m) }));
        Expect(RuntimeIslandBinder.ResolveBalance(snapSingle) == "¥12.50", "Single CNY entry resolves to ¥12.50");

        // Single zero entry
        var snapZero = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Balance: new AccountBalanceSnapshot(true, new[] { new AccountBalanceEntry("CNY", 0.00m, 0m, 0.00m) }));
        Expect(RuntimeIslandBinder.ResolveBalance(snapZero) == "¥0.00", "Single zero CNY entry resolves to ¥0.00");

        // Single negative entry
        var snapNeg = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Balance: new AccountBalanceSnapshot(true, new[] { new AccountBalanceEntry("CNY", -5.40m, 0m, -5.40m) }));
        Expect(RuntimeIslandBinder.ResolveBalance(snapNeg) == "-¥5.40", "Single negative CNY entry resolves to -¥5.40");

        // Multi entries (CNY + USD)
        var snapMulti = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Balance: new AccountBalanceSnapshot(true, new[]
            {
                new AccountBalanceEntry("CNY", 12.50m, 0m, 12.50m),
                new AccountBalanceEntry("USD", 5.00m, 0m, 5.00m),
            }));
        Expect(RuntimeIslandBinder.ResolveBalance(snapMulti) == "CNY ¥12.50 · USD $5.00", "Multi entries resolve joined with middot");

        Console.WriteLine("PASS TestResolveBalanceSingleAndMultiEntry");
    }

    private static void TestSlotBalanceIsolationFromQuotaAndCost()
    {
        var now = DateTimeOffset.Now;

        // 1. Balance only -> BalanceText set, QuotaText and CostText empty, HasBalanceText true
        var balanceOnlySnap = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Idle, SnapshotAvailability.Ready, now, now,
            Balance: new AccountBalanceSnapshot(true, new[] { new AccountBalanceEntry("CNY", 18.80m, 0m, 18.80m) }));

        var slot = RuntimeIslandBinder.CreateSlotViewModel(balanceOnlySnap);
        Expect(slot.BalanceText == "¥18.80", "Slot BalanceText matches formatted balance");
        Expect(slot.HasBalanceText, "HasBalanceText is true");
        Expect(slot.QuotaText == string.Empty, "QuotaText is empty when no Usage");
        Expect(!slot.HasQuotaText, "HasQuotaText is false");
        Expect(slot.CostText == string.Empty, "CostText is empty when no Cost");
        Expect(!slot.HasCostText, "HasCostText is false");
        Expect(slot.MetricsText == "¥18.80", "MetricsText falls back to BalanceText when QuotaText is empty");

        var changed = new List<string?>();
        slot.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        slot.CostText = "$0.01";
        Expect(changed.Contains(nameof(ProviderSlotViewModel.MetricsText)),
            "MetricsText raises PropertyChanged when fallback CostText changes");

        // 2. Combined Usage + Cost + Balance -> All three coexist independently without contamination
        var usage = new AppUsage(
            FiveHour: new WindowUsage(0.65, now.AddHours(2), null),
            Weekly: new WindowUsage(0.20, now.AddDays(3), null));

        var cost = new ProviderCostSummary(
            3.50, 50000, 50000, 10.00, 150000, 150000,
            new double[24], Array.Empty<double>(),
            Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(), Array.Empty<ModelSpend>(),
            Array.Empty<DailyTokenBucket>(), Array.Empty<string>());

        var allSnap = new AgentSnapshot((AgentKey)"deepseek", ActivityState.Working, SnapshotAvailability.Ready, now, now,
            Usage: usage,
            Cost: cost,
            Balance: new AccountBalanceSnapshot(true, new[] { new AccountBalanceEntry("CNY", 99.00m, 0m, 99.00m) }));

        var allSlot = RuntimeIslandBinder.CreateSlotViewModel(allSnap);
        Expect(allSlot.Status == ProviderSlotStatus.Working, "Status is Working");
        Expect(allSlot.QuotaText == "65%", "QuotaText matches Usage FiveHour percent");
        Expect(allSlot.HasQuotaText, "HasQuotaText is true");
        Expect(allSlot.CostText == "$3.50", "CostText matches Cost TodayDollars");
        Expect(allSlot.HasCostText, "HasCostText is true");
        Expect(allSlot.BalanceText == "¥99.00", "BalanceText matches Balance entry");
        Expect(allSlot.HasBalanceText, "HasBalanceText is true");
        Expect(allSlot.MetricsText == "65%", "MetricsText prioritizes QuotaText when available");

        Console.WriteLine("PASS TestSlotBalanceIsolationFromQuotaAndCost");
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
