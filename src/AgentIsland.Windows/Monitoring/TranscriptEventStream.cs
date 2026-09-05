using System.IO;
using AgentIsland.Core;
using AgentIsland.Windows;

namespace AgentIsland.Windows.Monitoring;

/// File-event watcher over the transcript roots — the Windows counterpart of
/// the macOS FSEvents stream. Polling alone means a state change waits up to
/// a full tick to surface; file events let the monitor react the moment a
/// transcript line lands, so the logo starts and stops with the run instead
/// of seconds behind it. The poll stays as a fallback sweep.
public sealed class TranscriptEventStream : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Action _onChange;

    public TranscriptEventStream(Action onChange)
    {
        _onChange = onChange;
    }

    public void Start(IReadOnlySet<TriggerTool>? providers = null)
    {
        if (_watchers.Count > 0) return;
        var roots = new List<string>();
        bool IsEnabled(TriggerTool provider) => providers is null || providers.Contains(provider);
        if (IsEnabled(TriggerTool.Claude))
        {
            roots.AddRange(IslandPaths.ClaudeProjectRoots);
            roots.Add(IslandPaths.ClaudeDesktopSessionsRoot);
        }
        if (IsEnabled(TriggerTool.Codex))
            roots.Add(IslandPaths.CodexSessionsRoot);
        if (IsEnabled(TriggerTool.DeepSeek))
            roots.Add(IslandPaths.DeepSeekSessionsRoot);
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                // Buffer overflow drops individual events; a full rescan is the
                // correct recovery either way.
                watcher.Error += (_, _) => _onChange();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch
            {
                // A root that cannot be watched still gets covered by the poll.
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.FullPath)) _onChange();
    }

    private static bool IsRelevant(string path)
    {
        if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".jsonl.zstd", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(path);
        return name.StartsWith("local_", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
            }
        }
        _watchers.Clear();
    }
}
