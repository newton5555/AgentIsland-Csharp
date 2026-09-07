using System.Collections.Concurrent;
using System.ComponentModel;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Backend.Settings;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Threading;
using AgentIsland.UI.Providers;
using Xunit;

namespace AgentIsland.Tests;

public sealed class ActivityMonitorConcurrencyTests
{
    private sealed class DedicatedThreadDispatcher : IUiDispatcher, IDisposable
    {
        private readonly Thread _thread;
        private readonly BlockingCollection<Action> _queue = new();
        private readonly CancellationTokenSource _cts = new();

        public DedicatedThreadDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ActivityMonitorTestDispatcher"
            };
            _thread.Start();
        }

        private void Run()
        {
            try
            {
                while (!_cts.IsCancellationRequested && !_queue.IsCompleted)
                {
                    if (_queue.TryTake(out var action, 100, _cts.Token))
                    {
                        try
                        {
                            action();
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public bool CheckAccess() => Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId;
        public bool FailInvocations { get; set; }

        public void Invoke(Action action)
        {
            if (FailInvocations)
            {
                throw new InvalidOperationException("Dispatcher is shutting down");
            }
            if (CheckAccess())
            {
                action();
                return;
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            tcs.Task.GetAwaiter().GetResult();
        }

        public Task InvokeAsync(Action action)
        {
            if (FailInvocations)
            {
                throw new InvalidOperationException("Dispatcher is shutting down");
            }
            if (CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        public Task InvokeAsync(Func<Task> asyncAction)
        {
            if (FailInvocations)
            {
                throw new InvalidOperationException("Dispatcher is shutting down");
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(async () =>
            {
                try
                {
                    await asyncAction();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        public void BeginInvoke(Action action)
        {
            if (FailInvocations)
            {
                throw new InvalidOperationException("Dispatcher is shutting down");
            }
            try
            {
                if (!_cts.IsCancellationRequested && !_queue.IsAddingCompleted)
                {
                    _queue.Add(action);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _queue.CompleteAdding(); } catch { }
            _thread.Join(2000);
            _cts.Dispose();
            _queue.Dispose();
        }
    }

    private sealed class TestVisibilityStore : IProviderVisibilityStore
    {
        private readonly List<DisplayProvider> _enabled;
        public event PropertyChangedEventHandler? PropertyChanged;

        public TestVisibilityStore(params DisplayProvider[] enabled)
        {
            _enabled = new List<DisplayProvider>(enabled);
        }

        public IReadOnlyList<DisplayProvider> Enabled => _enabled;
        public IReadOnlyList<DisplayProvider> SlotProviders => _enabled;
        public IReadOnlyList<DisplayProvider> Slots => _enabled;
        public IReadOnlyList<DisplayProvider> Order => _enabled;
        public bool ClaudeVisible
        {
            get => _enabled.Contains(DisplayProvider.Claude);
            set => SetEnabled(DisplayProvider.Claude, value);
        }
        public bool CodexVisible
        {
            get => _enabled.Contains(DisplayProvider.Codex);
            set => SetEnabled(DisplayProvider.Codex, value);
        }
        public bool ClaudeShown => ClaudeVisible;
        public bool CodexShown => CodexVisible;
        public bool ClaudePanelShown => ClaudeVisible;
        public bool CodexPanelShown => CodexVisible;
        public bool AntigravityPanelShown => _enabled.Contains(DisplayProvider.Antigravity);
        public bool GrokPanelShown => _enabled.Contains(DisplayProvider.Grok);
        public bool CursorPanelShown => _enabled.Contains(DisplayProvider.Cursor);
        public bool DeepSeekPanelShown => _enabled.Contains(DisplayProvider.DeepSeek);
        public int GuestPanelCount => 0;
        public bool IsVisible(TriggerTool tool) => true;
        public void RedetectGuests() { }
        public int SelectedCount => _enabled.Count;
        public bool ClaudeDetected => true;
        public bool CodexDetected => true;
        public bool AntigravityDetected => true;
        public bool GrokDetected => false;
        public bool CursorDetected => false;
        public bool DeepSeekDetected => false;
        public bool SetEnabled(DisplayProvider provider, bool enabled)
        {
            if (enabled && !_enabled.Contains(provider))
            {
                _enabled.Add(provider);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                return true;
            }
            if (!enabled && _enabled.Remove(provider))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                return true;
            }
            return false;
        }
        public void MoveProvider(int oldIndex, int newIndex) { }
        public bool IsShown(DisplayProvider provider) => _enabled.Contains(provider);
        public bool IsEnabled(DisplayProvider provider) => _enabled.Contains(provider);
        public bool Toggle(DisplayProvider provider) => SetEnabled(provider, !_enabled.Contains(provider));
    }

    private sealed class TestSensorProvider : IAgentProvider, ISessionSensor
    {
        public AgentDescriptor Descriptor { get; }
        public Func<DateTimeOffset, IReadOnlyDictionary<string, DateTimeOffset>, CancellationToken, ValueTask<IReadOnlyList<ScannedSession>>> Handler { get; set; }

        public TestSensorProvider(string key, string name, Func<DateTimeOffset, IReadOnlyDictionary<string, DateTimeOffset>, CancellationToken, ValueTask<IReadOnlyList<ScannedSession>>> handler)
        {
            Descriptor = new AgentDescriptor(new AgentKey(key), name, AgentCapabilities.Activity);
            Handler = handler;
        }

        public ValueTask<IReadOnlyList<ScannedSession>> ScanSessionsAsync(
            DateTimeOffset now,
            IReadOnlyDictionary<string, DateTimeOffset> lastWorking,
            CancellationToken ct = default) => Handler(now, lastWorking, ct);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), $"Condition not met within {timeoutMs}ms");
    }

    [Fact]
    public async Task Scenario1_ThreadPoolScanNow_ConcurrentWithUiCommit_DoesNotCorruptOrLoseCoordinationState()
    {
        using var dispatcher = new DedicatedThreadDispatcher();
        var scanCount = 0;
        var now = DateTimeOffset.UtcNow;
        var workingSession = new ScannedSession(
            TriggerTool.Claude,
            "session-1",
            @"C:\project",
            "project",
            now,
            ActivityState.Working,
            @"C:\project\transcript.jsonl",
            "claude:1",
            SessionLaunchTarget.Cli);

        var claudeProvider = new TestSensorProvider("claude", "Claude", async (n, lw, ct) =>
        {
            Interlocked.Increment(ref scanCount);
            await Task.Delay(15, ct);
            return new[] { workingSession };
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var monitor = new ActivityMonitor(
            visibilityStore: visibility,
            reminderCenter: null,
            uiDispatcher: dispatcher,
            providers: new[] { claudeProvider })
        {
            DisableInternalTimer = true
        };

        monitor.Start();

        // Launch multiple ThreadPool workers calling ScanNow concurrently
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
            {
                monitor.ScanNow();
                await Task.Delay(5);
            }
        })).ToArray();

        await Task.WhenAll(workers);

        // Perform verifiable scan completion
        await monitor.ScanNowAsync();

        // State must be consistently committed to Working and Claude must be present in States
        Assert.True(scanCount >= 1, "At least one scan must have completed");
        Assert.Equal(ActivityState.Working, monitor.Claude);
        Assert.True(monitor.States.ContainsKey(TriggerTool.Claude));
        Assert.Equal(ActivityState.Working, monitor.States[TriggerTool.Claude]);
        Assert.NotNull(monitor.ClaudeThread);
        monitor.Stop();
    }

    [Fact]
    public async Task Scenario2_ProviderSwitch_WhileScanInFlight_OldResultsNotPublished()
    {
        using var dispatcher = new DedicatedThreadDispatcher();
        var claudeScanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claudeGate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codexScanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var codexGate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var claudeInvocations = 0;
        var codexInvocations = 0;

        var claudeProvider = new TestSensorProvider("claude", "Claude", (n, lw, ct) =>
        {
            Interlocked.Increment(ref claudeInvocations);
            claudeScanStarted.TrySetResult();
            return new ValueTask<IReadOnlyList<ScannedSession>>(claudeGate.Task);
        });

        var codexProvider = new TestSensorProvider("codex", "Codex", (n, lw, ct) =>
        {
            Interlocked.Increment(ref codexInvocations);
            codexScanStarted.TrySetResult();
            return new ValueTask<IReadOnlyList<ScannedSession>>(codexGate.Task);
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var monitor = new ActivityMonitor(
            visibilityStore: visibility,
            reminderCenter: null,
            uiDispatcher: dispatcher,
            providers: new[] { claudeProvider, codexProvider })
        {
            DisableInternalTimer = true
        };

        monitor.Start();
        monitor.ScanNow();

        // 1. Wait until Claude scan is actively executing in background
        await claudeScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, claudeInvocations);
        Assert.Equal(0, codexInvocations);

        // 2. Switch provider while Claude scan is in-flight: disable Claude, enable Codex
        visibility.SetEnabled(DisplayProvider.Claude, false);
        visibility.SetEnabled(DisplayProvider.Codex, true);

        // Allow dispatcher to process ApplyProviderMode
        await WaitForConditionAsync(() => !monitor.States.ContainsKey(TriggerTool.Claude));

        // 3. Complete the stale Claude scan late with a Working session
        var now = DateTimeOffset.UtcNow;
        var staleSession = new ScannedSession(
            TriggerTool.Claude,
            "stale-1",
            @"C:\project",
            "project",
            now,
            ActivityState.Working,
            @"C:\project\transcript.jsonl",
            "claude:1",
            SessionLaunchTarget.Cli);

        claudeGate.SetResult(new[] { staleSession });

        // 4. Codex sensor MUST now be invoked because visibility switched to Codex
        await codexScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, codexInvocations);

        // 5. Complete Codex scan with a Working session
        var codexSession = new ScannedSession(
            TriggerTool.Codex,
            "codex-1",
            @"C:\project",
            "project",
            now,
            ActivityState.Working,
            @"C:\project\transcript.jsonl",
            "codex:1",
            SessionLaunchTarget.Cli);

        codexGate.SetResult(new[] { codexSession });

        // 6. Wait for Codex result to be published
        await WaitForConditionAsync(() => monitor.Codex == ActivityState.Working);

        // Assertions:
        // - Codex is published
        Assert.Equal(ActivityState.Working, monitor.Codex);
        Assert.True(monitor.States.ContainsKey(TriggerTool.Codex));
        Assert.Equal(ActivityState.Working, monitor.States[TriggerTool.Codex]);
        Assert.NotNull(monitor.CodexThread);

        // - Claude's late results MUST NOT be published
        Assert.False(monitor.States.ContainsKey(TriggerTool.Claude), "Claude must not be present in States after being disabled");
        Assert.Equal(ActivityState.Idle, monitor.Claude);
        Assert.Null(monitor.ClaudeThread);

        monitor.Stop();
    }

    [Fact]
    public async Task Scenario3_Stop_WhileScanInFlight_OldResultsNotPublished_AndResourcesSafelyCleanedUp()
    {
        using var dispatcher = new DedicatedThreadDispatcher();
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanGate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedCt = default;

        var claudeProvider = new TestSensorProvider("claude", "Claude", (n, lw, ct) =>
        {
            capturedCt = ct;
            scanStarted.TrySetResult();
            return new ValueTask<IReadOnlyList<ScannedSession>>(scanGate.Task);
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var monitor = new ActivityMonitor(
            visibilityStore: visibility,
            reminderCenter: null,
            uiDispatcher: dispatcher,
            providers: new[] { claudeProvider })
        {
            DisableInternalTimer = true
        };

        monitor.Start();
        monitor.ScanNow();

        await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Stop while scan is in-flight (called cross-thread from test runner)
        monitor.Stop();

        // CTS must be canceled
        Assert.True(capturedCt.IsCancellationRequested, "In-flight scan CTS must be canceled on Stop");

        // Monitor must be stopped and states cleared
        Assert.Empty(monitor.States);
        Assert.Equal(ActivityState.Idle, monitor.Claude);

        // Complete the in-flight scan late with a Working session
        var now = DateTimeOffset.UtcNow;
        var lateSession = new ScannedSession(
            TriggerTool.Claude,
            "late-1",
            @"C:\project",
            "project",
            now,
            ActivityState.Working,
            @"C:\project\transcript.jsonl",
            "claude:1",
            SessionLaunchTarget.Cli);

        scanGate.SetResult(new[] { lateSession });

        await Task.Delay(100);

        // State must remain completely cleared, not resurrected by late result
        Assert.Empty(monitor.States);
        Assert.Equal(ActivityState.Idle, monitor.Claude);
        Assert.Null(monitor.ClaudeThread);
    }

    [Fact]
    public async Task Scenario4_TrailingScan_SemanticsHold_KicksCoalesceIntoSingleFollowUp()
    {
        using var dispatcher = new DedicatedThreadDispatcher();
        var invocationCount = 0;
        var scan1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan1Gate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan2Gate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var now = DateTimeOffset.UtcNow;
        var claudeProvider = new TestSensorProvider("claude", "Claude", (n, lw, ct) =>
        {
            var count = Interlocked.Increment(ref invocationCount);
            if (count == 1)
            {
                scan1Started.TrySetResult();
                return new ValueTask<IReadOnlyList<ScannedSession>>(scan1Gate.Task);
            }
            if (count == 2)
            {
                scan2Started.TrySetResult();
                return new ValueTask<IReadOnlyList<ScannedSession>>(scan2Gate.Task);
            }
            throw new InvalidOperationException($"Unexpected invocation count: {count}");
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var monitor = new ActivityMonitor(
            visibilityStore: visibility,
            reminderCenter: null,
            uiDispatcher: dispatcher,
            providers: new[] { claudeProvider })
        {
            DisableInternalTimer = true
        };

        monitor.Start();
        monitor.ScanNow();

        // Wait for scan 1 to be in flight
        await scan1Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, invocationCount);

        // Multiple kicks land during scan 1
        monitor.ScanNow();
        monitor.ScanNow();
        monitor.ScanNow();
        monitor.ScanNow();
        monitor.ScanNow();

        // Coalescing: must still be exactly 1 scan in flight
        Assert.Equal(1, invocationCount);

        // Complete scan 1 with Idle session
        var idleSession = new ScannedSession(
            TriggerTool.Claude,
            "idle-1",
            @"C:\project",
            "project",
            now,
            ActivityState.Idle,
            null,
            null,
            SessionLaunchTarget.Cli);
        scan1Gate.SetResult(new[] { idleSession });

        // Trailing scan must now fire
        await scan2Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, invocationCount);

        // Complete scan 2 with Working session
        var workingSession = new ScannedSession(
            TriggerTool.Claude,
            "work-2",
            @"C:\project",
            "project",
            now.AddSeconds(1),
            ActivityState.Working,
            @"C:\project\transcript.jsonl",
            "turn-2",
            SessionLaunchTarget.Cli);
        scan2Gate.SetResult(new[] { workingSession });

        // Wait for commit of trailing scan
        await WaitForConditionAsync(() => monitor.Claude == ActivityState.Working);

        // Exactly 2 invocations happened (kicks coalesced into 1 trailing scan, no scan 3)
        Assert.Equal(2, invocationCount);
        Assert.Equal(ActivityState.Working, monitor.Claude);
        monitor.Stop();
    }

    [Fact]
    public async Task Scenario5_ScanNowAsync_ThreadPoolAwaitsCommit_AndNoDeadlockOnDispatcherThread()
    {
        using var dispatcher = new DedicatedThreadDispatcher();
        var now = DateTimeOffset.UtcNow;
        var workingSession = new ScannedSession(
            TriggerTool.Claude,
            "session-5",
            @"C:\project",
            "project",
            now,
            ActivityState.Working,
            @"C:\project\transcript.jsonl",
            "claude:5",
            SessionLaunchTarget.Cli);

        var claudeProvider = new TestSensorProvider("claude", "Claude", async (n, lw, ct) =>
        {
            await Task.Delay(20, ct);
            return new[] { workingSession };
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var monitor = new ActivityMonitor(
            visibilityStore: visibility,
            reminderCenter: null,
            uiDispatcher: dispatcher,
            providers: new[] { claudeProvider })
        {
            DisableInternalTimer = true
        };

        monitor.Start();

        // 1. ThreadPool caller awaiting ScanNowAsync: UI state must be committed upon return
        await Task.Run(async () =>
        {
            await monitor.ScanNowAsync();
            Assert.Equal(ActivityState.Working, monitor.Claude);
        });

        // 2. Dispatcher thread caller awaiting ScanNowAsync: no synchronous deadlock
        var uiTask = dispatcher.InvokeAsync(async () =>
        {
            await monitor.ScanNowAsync();
            Assert.Equal(ActivityState.Working, monitor.Claude);
        });

        await uiTask.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Stop();
    }

    [Fact]
    public async Task Scenario6_MultipleSwitchesAndKicks_CoalesceIntoSingleFollowUpScan()
    {
        using var dispatcher = new DedicatedThreadDispatcher();
        var claudeScanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claudeGate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codexInvocations = 0;
        var deepSeekScanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deepSeekGate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deepSeekInvocations = 0;

        var claudeProvider = new TestSensorProvider("claude", "Claude", (n, lw, ct) =>
        {
            claudeScanStarted.TrySetResult();
            return new ValueTask<IReadOnlyList<ScannedSession>>(claudeGate.Task);
        });

        var codexProvider = new TestSensorProvider("codex", "Codex", (n, lw, ct) =>
        {
            Interlocked.Increment(ref codexInvocations);
            return new ValueTask<IReadOnlyList<ScannedSession>>(Array.Empty<ScannedSession>());
        });

        var deepSeekProvider = new TestSensorProvider("deepseek", "DeepSeek", (n, lw, ct) =>
        {
            Interlocked.Increment(ref deepSeekInvocations);
            deepSeekScanStarted.TrySetResult();
            return new ValueTask<IReadOnlyList<ScannedSession>>(deepSeekGate.Task);
        });

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var monitor = new ActivityMonitor(
            visibilityStore: visibility,
            reminderCenter: null,
            uiDispatcher: dispatcher,
            providers: new[] { claudeProvider, codexProvider, deepSeekProvider })
        {
            DisableInternalTimer = true
        };

        monitor.Start();
        monitor.ScanNow();

        // 1. Wait until Claude scan is running in background
        await claudeScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // 2. Switch to Codex while Claude is in-flight
        visibility.SetEnabled(DisplayProvider.Claude, false);
        visibility.SetEnabled(DisplayProvider.Codex, true);

        // Multiple kicks land during in-flight scan
        monitor.ScanNow();
        monitor.ScanNow();

        // 3. Switch to DeepSeek before Claude completes
        visibility.SetEnabled(DisplayProvider.Codex, false);
        visibility.SetEnabled(DisplayProvider.DeepSeek, true);

        monitor.ScanNow();

        // 4. Complete stale Claude scan
        var now = DateTimeOffset.UtcNow;
        claudeGate.SetResult(new[] {
            new ScannedSession(
                TriggerTool.Claude,
                "stale-claude",
                @"C:\project",
                "project",
                now,
                ActivityState.Working,
                @"C:\project\transcript.jsonl",
                "claude:1",
                SessionLaunchTarget.Cli)
        });

        // 5. DeepSeek sensor must be invoked (not Codex, which was intermediate)
        await deepSeekScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, codexInvocations);
        Assert.Equal(1, deepSeekInvocations);

        // 6. Complete DeepSeek
        deepSeekGate.SetResult(new[] {
            new ScannedSession(
                TriggerTool.DeepSeek,
                "deepseek-1",
                @"C:\project",
                "project",
                now,
                ActivityState.Working,
                @"C:\project\transcript.jsonl",
                "deepseek:1",
                SessionLaunchTarget.Cli)
        });

        await WaitForConditionAsync(() => monitor.DeepSeek == ActivityState.Working);

        Assert.Equal(ActivityState.Working, monitor.DeepSeek);
        Assert.False(monitor.States.ContainsKey(TriggerTool.Claude));
        Assert.False(monitor.States.ContainsKey(TriggerTool.Codex));
        Assert.Equal(1, deepSeekInvocations);

        monitor.Stop();
    }

    [Fact]
    public async Task Scenario7_Stop_WhenDispatcherUnavailable_CancelsBackgroundOperationsSafely()
    {
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanGate = new TaskCompletionSource<IReadOnlyList<ScannedSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedCt = default;

        var claudeProvider = new TestSensorProvider("claude", "Claude", (n, lw, ct) =>
        {
            capturedCt = ct;
            scanStarted.TrySetResult();
            return new ValueTask<IReadOnlyList<ScannedSession>>(scanGate.Task);
        });

        using var dispatcher = new DedicatedThreadDispatcher();

        var visibility = new TestVisibilityStore(DisplayProvider.Claude);
        var monitor = new ActivityMonitor(
            visibilityStore: visibility,
            reminderCenter: null,
            uiDispatcher: dispatcher,
            providers: new[] { claudeProvider })
        {
            DisableInternalTimer = true
        };

        monitor.Start();
        monitor.ScanNow();

        await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Simulate dispatcher shutting down
        dispatcher.FailInvocations = true;

        // Calling Stop must not throw, must cancel background CTS safely
        monitor.Stop();

        Assert.True(capturedCt.IsCancellationRequested, "In-flight scan CTS must be canceled on Stop");

        // Complete the sensor gate late
        scanGate.SetResult(Array.Empty<ScannedSession>());
    }
}
