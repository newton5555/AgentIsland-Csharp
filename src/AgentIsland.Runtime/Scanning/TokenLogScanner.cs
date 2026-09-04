using AgentIsland.Core;
using AgentIsland.Core.Cost;

namespace AgentIsland.Runtime.Scanning;

/// Host-neutral log scanner that pairs a provider's file parser with Core's
/// (mtime, size) cache. Path discovery, directory rules, and cache location are
/// supplied by the host so this scanner remains portable across desktop platforms.
public sealed class TokenLogScanner
{
    private readonly LogParseCache _cache;
    private readonly Func<string, List<TokenEvent>> _parser;
    private readonly object _gate = new();

    public TokenLogScanner(
        TriggerTool provider,
        string cacheFilePath,
        Func<string, List<TokenEvent>> parser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFilePath);
        ArgumentNullException.ThrowIfNull(parser);

        _cache = new LogParseCache(provider, cacheFilePath);
        _parser = parser;
    }

    /// Scans the given files for token usage events newer than or equal to <paramref name="cutoff"/>.
    /// Paths are deduplicated before scanning. Cancellation is checked per file and propagated immediately.
    public List<TokenEvent> Scan(
        IEnumerable<string> files,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _cache.Walk(
                DeduplicateFiles(files, cancellationToken),
                cutoff,
                path => SafeParse(path, cancellationToken));
        }
    }

    /// Asynchronous wrapper around <see cref="Scan"/> using thread pool execution.
    /// Does not create dedicated background threads or UI timers.
    public Task<List<TokenEvent>> ScanAsync(
        IEnumerable<string> files,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => Scan(files, cutoff, cancellationToken), cancellationToken);
    }

    private static IEnumerable<string> DeduplicateFiles(
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file)) continue;

            string normalized;
            try
            {
                normalized = Path.GetFullPath(file);
            }
            catch
            {
                normalized = file;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private List<TokenEvent> SafeParse(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var events = _parser(path);
            cancellationToken.ThrowIfCancellationRequested();
            return events ?? new List<TokenEvent>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            // Follow LogParseCache / parser tolerance: unreadable or partially written
            // files yield no events for this cycle and retry on subsequent scans.
            return new List<TokenEvent>();
        }
    }
}
