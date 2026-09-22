using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentIsland.Backend.Alarms;
using AgentIsland.Backend.Monitoring;
using AgentMonitoring.Activity;
using AgentMonitoring.Enablement;
using AgentMonitoring.Navigation;
using AgentMonitoring.Notifications;
using AgentMonitoring.Queries;
using AgentMonitoring.Runtime;

namespace AgentIsland.Tests;

public class P5ActivityReminderTests
{
    [Fact]
    public void ActivityReducer_TracksAgentKeyWithoutUiEnum()
    {
        var now = DateTimeOffset.UtcNow;
        var nova = new AgentKey("nova");
        var session = new ScannedSession(
            nova, "sess-1", "/proj", "Nova", now, ActivityState.NeedsYou, @"C:\n.jsonl", "turn-1",
            SessionLaunchTarget.Cli);
        var activity = ActivityReducer.Reduce(nova, new[] { session }, _ => false, now);
        Assert.Equal(ActivityState.NeedsYou, activity.State);
        Assert.Equal("sess-1", activity.Thread!.SessionId);
        Assert.Single(activity.NeedsYouThreads);
        Assert.Null(nova.ToDisplayProvider());
    }

    [Fact]
    public void ActivityReducer_WorkingOutranksAcknowledgedNeedsYou()
    {
        var now = DateTimeOffset.UtcNow;
        var agent = AgentKeys.Claude;
        var done = new ScannedSession(
            agent, "a", "/p", "A", now, ActivityState.NeedsYou, "a.jsonl", "t1", SessionLaunchTarget.Cli);
        var work = new ScannedSession(
            agent, "b", "/p", "B", now.AddSeconds(1), ActivityState.Working, "b.jsonl", null, SessionLaunchTarget.Cli);
        var activity = ActivityReducer.Reduce(
            agent, new[] { done, work },
            thread => thread.SessionId == "a",
            now);
        Assert.Equal(ActivityState.Working, activity.State);
        Assert.Equal("b", activity.Thread!.SessionId);
    }

    [Fact]
    public void ReminderCenter_EmptyFirstScanDoesNotSwallowLaterNeedsYou()
    {
        var store = new AgentReminderStore();
        var alarms = new RecordingAlarms();
        var center = new AgentReminderCenter(store, alarms);
        center.Observe(AgentKeys.Claude);
        var thread = new ActivityMonitor.ActiveThread(
            "sess", "Claude", "/p", DateTimeOffset.Now.AddMinutes(1), "t.jsonl", "turn-1",
            SessionLaunchTarget.Cli);
        center.Handle(TriggerTool.Claude, new[] { thread });
        Assert.False(center.HasAcknowledged(TriggerTool.Claude, thread));
    }

    [Fact]
    public void ReminderBroker_DoesNotReplayAcknowledgedTurn()
    {
        var broker = new ReminderBroker();
        var agent = new AgentKey("nova");
        var thread = new ActivityThread("s", "Nova", "/p", DateTimeOffset.UtcNow, "n.jsonl", "turn-1", SessionLaunchTarget.Cli);
        var first = broker.Diff(agent, new[] { thread });
        Assert.Single(first.Raised);
        broker.Acknowledge(first.Raised[0].DeliveryKey);
        var second = broker.Diff(agent, new[] { thread });
        Assert.Empty(second.Raised);
        Assert.Empty(second.Dismissed);
        var gone = broker.Diff(agent, Array.Empty<ActivityThread>());
        Assert.Single(gone.Dismissed);
    }

    [Fact]
    public void ReminderKeys_MatchLegacyDeliveryKey()
    {
        var thread = new ActivityThread("sid", "Label", "/cwd", DateTimeOffset.UtcNow, @"C:\t.jsonl", "turn-9", SessionLaunchTarget.Cli);
        var next = ReminderKeys.ForNeedsYou(AgentKeys.Claude, thread);
        var legacy = ReminderDeliveryKey.Make(
            "claude", (int)ActivityState.NeedsYou, thread.TranscriptPath, thread.SessionId, thread.Cwd, thread.Label, thread.TurnKey);
        Assert.Equal(legacy, next);
    }

    [Fact]
    public async Task AgentRuntime_CollectsByAgentKey()
    {
        var snapshots = new LedgerSnapshotStore();
        var reader = new StubLedger();
        var runtime = new AgentRuntime(
            snapshots,
            AlwaysEnabledAgents.Instance,
            new[] { new StubCostProvider(new AgentKey("nova"), reader) });
        var result = await runtime.CollectCostAsync(new AgentKey("nova"), 30, DateTimeOffset.UtcNow);
        Assert.Equal("nova", result.Agent.Value);
        Assert.Equal(1, reader.Reads);
        Assert.NotNull(snapshots.Read(new AgentKey("nova")));
        Assert.Null(new AgentKey("nova").ToDisplayProvider());
    }

    [Fact]
    public void Overview_IncludesActivitySnapshot()
    {
        var ledgers = new LedgerSnapshotStore();
        var activity = new ActivitySnapshotStore();
        var agent = new AgentKey("nova");
        activity.Replace(new AgentActivity(
            agent, ActivityState.Working, null, Array.Empty<ActivityThread>(), DateTimeOffset.Parse("2026-07-16T12:00:00Z")));
        var query = new MonitoringQueryService(ledgers, activity: activity);
        var overview = query.GetOverview(agent);
        Assert.Equal(ActivityState.Working, overview.Activity);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:00:00Z"), overview.ActivityAt);
    }

    [Fact]
    public void NavigationTarget_CarriesSemanticLaunch()
    {
        var target = new NavigationTarget(new AgentKey("nova"), "s", "/p", SessionLaunchTarget.Cli, "n.jsonl");
        Assert.Equal("nova", target.Agent.Value);
        Assert.Equal(SessionLaunchTarget.Cli, target.Launch);
    }

    private sealed class RecordingAlarms : ITurnAlarmWindowController
    {
        public void Show(TriggerTool provider, ActivityMonitor.ActiveThread? thread, string deliveryKey, TurnAlarmKind? kind = null) { }
        public void AutoDismiss(TriggerTool provider, string deliveryKey) { }
    }

    private sealed class StubLedger : ICostLedgerReader
    {
        public int Reads;
        public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default)
        {
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<TokenEvent>>(Array.Empty<TokenEvent>());
        }
    }

    private sealed class StubCostProvider : IAgentProvider, ICostLedgerReader
    {
        private readonly ICostLedgerReader _reader;
        public StubCostProvider(AgentKey key, ICostLedgerReader reader)
        {
            Descriptor = new AgentDescriptor(key, key.Value, AgentCapabilities.Cost);
            _reader = reader;
        }
        public AgentDescriptor Descriptor { get; }
        public ICostLedgerReader? CostLedgerReader => _reader;
        public ValueTask<IReadOnlyList<TokenEvent>> ReadCostEventsAsync(int lookbackDays = 30, CancellationToken ct = default) =>
            _reader.ReadCostEventsAsync(lookbackDays, ct);
    }
}
