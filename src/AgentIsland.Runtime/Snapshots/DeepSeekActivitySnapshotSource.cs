using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Providers.Sessions.DeepSeek;
using AgentIsland.Runtime.Sources;

namespace AgentIsland.Runtime.Snapshots;

/// <summary>
/// Cross-platform snapshot source that reads local DeepSeek Harness session files
/// (~/.dsh/sessions/**/session.jsonl.zstd) to detect real-time activity (Working vs Idle).
///
/// Rules:
/// 1. Reads local session streams, skipping delegated subagent transcripts (IsSubagent).
/// 2. Resolves each human session's activity timestamp from its latest event timestamp or file mtime.
/// 3. If the latest human session event is Active and within the active window (default: 18 seconds),
///    the agent state is Working; otherwise Idle.
/// 4. If no valid human sessions exist, availability is NoData. If at least one exists, availability is Ready.
/// 5. Strictly local and offline: no credentials or network calls.
/// 6. Tolerant of missing, torn, or vanishing files during scans.
/// </summary>
public sealed class DeepSeekActivitySnapshotSource : IAgentSnapshotSource
{
    public static readonly TimeSpan DefaultActiveWindow = TimeSpan.FromSeconds(18);

    private readonly Func<IEnumerable<string>> _fileProvider;
    private readonly Func<string, DeepSeekActivitySnapshot?> _parser;
    private readonly TimeSpan _activeWindow;

    public AgentKey Agent { get; }

    public DeepSeekActivitySnapshotSource(
        AgentKey? agentKey = null,
        Func<IEnumerable<string>>? fileProvider = null,
        Func<string, DeepSeekActivitySnapshot?>? parser = null,
        TimeSpan? activeWindow = null,
        string? sessionsRoot = null)
    {
        Agent = agentKey ?? (AgentKey)"deepseek";
        _fileProvider = fileProvider ?? (() => LocalTokenSources.EnumerateDeepSeekHarnessFiles(sessionsRoot));
        _parser = parser ?? DeepSeekActivityParser.ParseFile;
        _activeWindow = activeWindow ?? DefaultActiveWindow;
    }

    public Task<AgentSnapshot> ReadAsync(DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> files;
        try
        {
            files = _fileProvider();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new AgentSnapshot(
                Agent,
                ActivityState.Idle,
                SnapshotAvailability.Error,
                observedAt,
                observedAt,
                Error: ex.Message));
        }

        DateTimeOffset? latestActivityTime = null;
        DeepSeekActivityKind latestKind = DeepSeekActivityKind.None;
        var validHumanSessionCount = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DeepSeekActivitySnapshot? sessionSnapshot;
            try
            {
                sessionSnapshot = _parser(file);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Tolerant of disappearing or corrupt session files
                continue;
            }

            if (sessionSnapshot is null || sessionSnapshot.IsSubagent)
            {
                continue;
            }

            DateTimeOffset sessionTime = DateTimeOffset.MinValue;
            try
            {
                if (File.Exists(file))
                {
                    sessionTime = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                }
            }
            catch
            {
            }

            if (sessionSnapshot.LatestTimestamp is { } eventTime && eventTime > sessionTime)
            {
                sessionTime = eventTime;
            }

            if (sessionTime == DateTimeOffset.MinValue)
            {
                sessionTime = sessionSnapshot.LatestTimestamp ?? observedAt;
            }

            validHumanSessionCount++;

            if (latestActivityTime is null || sessionTime > latestActivityTime.Value)
            {
                latestActivityTime = sessionTime;
                latestKind = sessionSnapshot.LatestKind;
            }
        }

        if (validHumanSessionCount == 0)
        {
            return Task.FromResult(new AgentSnapshot(
                Agent,
                ActivityState.Idle,
                SnapshotAvailability.NoData,
                observedAt,
                observedAt));
        }

        var activityTime = latestActivityTime ?? observedAt;
        var age = observedAt - activityTime;
        var isWorking = latestKind == DeepSeekActivityKind.Active
            && age <= _activeWindow
            && age >= TimeSpan.FromSeconds(-5);

        var activityState = isWorking ? ActivityState.Working : ActivityState.Idle;

        return Task.FromResult(new AgentSnapshot(
            Agent,
            activityState,
            SnapshotAvailability.Ready,
            observedAt,
            activityTime));
    }
}
