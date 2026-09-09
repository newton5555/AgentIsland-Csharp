using System.IO;
using Microsoft.Data.Sqlite;
using AgentIsland.Core;
using AgentIsland.Core.Cost;
using AgentIsland.Providers.Cost.Antigravity;
using AgentIsland.Windows.Storage;

namespace AgentIsland.Backend.Cost;

/// Reads the current Antigravity SQLite conversation ledger. Newer AGY
/// versions store one database per conversation under the antigravity,
/// antigravity-cli and antigravity-ide roots. Each database contains
/// gen_metadata protobuf rows and, in newer builds, step metadata with retry
/// usage. Old encrypted .pb conversations are intentionally ignored because
/// they do not expose trustworthy token counters.
public static class AntigravityLogReader
{
    private static readonly LogParseCache Cache = new(
        TriggerTool.Antigravity,
        Path.Combine(IslandPaths.CacheDir, "antigravity-parse-cache.v1.json"));

    public static List<TokenEvent> Scan(int lookbackDays, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(0, lookbackDays));
        var files = IslandPaths.AntigravityConversationRoots
            .SelectMany(root => SafeFileSystem.EnumerateFiles(root, "*.db"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return new List<TokenEvent>();

        var events = Cache.Walk(files, cutoff, ParseFile, cancellationToken);
        return DeduplicateCrossRoot(events);
    }

    internal static void ClearMemoryCache() => Cache.ClearMemory();

    /// Test/diagnostic seam. The host cache calls this once per database.
    internal static List<TokenEvent> ParseFile(string path) => ParseDatabase(path);

    private static List<TokenEvent> ParseDatabase(string path)
    {
        var result = new List<ParsedEvent>();
        try
        {
            var fallback = FileTimestamp(path);
            var sessionId = Path.GetFileNameWithoutExtension(path);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            var trajectoryTimestamp = ReadTrajectoryTimestamp(connection);
            var generatorRows = ReadGeneratorRows(connection);
            var stepRows = ReadStepRows(connection);
            var identityTimestamps = new Dictionary<string, (DateTimeOffset Timestamp, int Rank)>(StringComparer.Ordinal);

            // Step metadata is the more detailed source in current databases;
            // generation metadata is retained as a fallback and for older
            // schemas. The per-file identity merge below prevents the two
            // representations from being counted twice.
            foreach (var metadata in stepRows)
            {
                var model = ContextModel(metadata.Model, metadata.ModelId);
                AddUsage(result, identityTimestamps, metadata.Usage, model, metadata.Provider,
                    metadata.TimestampUnixMilliseconds, trajectoryTimestamp, fallback, sessionId);
                foreach (var retry in metadata.RetryUsages)
                {
                    AddUsage(result, identityTimestamps, retry, model, metadata.Provider,
                        metadata.TimestampUnixMilliseconds, trajectoryTimestamp, fallback, sessionId);
                }
            }

            var generationModel = generatorRows
                .AsEnumerable()
                .Reverse()
                .Select(row => ContextModel(row.Model, row.ModelId))
                .FirstOrDefault(model => model is not null);
            var currentModel = generationModel;
            foreach (var metadata in generatorRows)
            {
                var rowModel = ContextModel(metadata.Model, metadata.ModelId);
                if (rowModel is not null) currentModel = rowModel;

                AddUsage(result, identityTimestamps, metadata.Usage, currentModel, null,
                    metadata.TimestampUnixMilliseconds, trajectoryTimestamp, fallback, sessionId);
                foreach (var retry in metadata.RetryUsages)
                {
                    AddUsage(result, identityTimestamps, retry, currentModel, null,
                        metadata.TimestampUnixMilliseconds, trajectoryTimestamp, fallback, sessionId);
                }
            }

            return MergeByIdentity(result).Select(item => item.Event).ToList();
        }
        catch
        {
            // AGY can append/rotate a database while the poll is reading it.
            // Return the last good cache entry and retry the changed file on a
            // later poll instead of taking down the cost worker.
            return new List<TokenEvent>();
        }
    }

    private static List<AntigravityLogParser.GeneratorMetadata> ReadGeneratorRows(
        SqliteConnection connection)
    {
        var output = new List<AntigravityLogParser.GeneratorMetadata>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, data FROM gen_metadata ORDER BY idx ASC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            try
            {
                output.Add(AntigravityLogParser.ParseGeneratorMetadata(reader.GetFieldValue<byte[]>(1)));
            }
            catch (FormatException)
            {
                // A newer row shape should not hide older valid rows in the
                // same conversation. The next AGY format can be added to the
                // provider parser without changing the database walk.
            }
        }
        return output;
    }

    private static List<AntigravityLogParser.StepMetadata> ReadStepRows(
        SqliteConnection connection)
    {
        if (!TableExists(connection, "steps")) return new List<AntigravityLogParser.StepMetadata>();

        var output = new List<AntigravityLogParser.StepMetadata>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, metadata FROM steps WHERE metadata IS NOT NULL ORDER BY idx ASC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            try
            {
                output.Add(AntigravityLogParser.ParseStepMetadata(reader.GetFieldValue<byte[]>(1)));
            }
            catch (FormatException)
            {
            }
        }
        return output;
    }

    private static DateTimeOffset? ReadTrajectoryTimestamp(SqliteConnection connection)
    {
        if (!TableExists(connection, "trajectory_metadata_blob")) return null;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM trajectory_metadata_blob ORDER BY rowid ASC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            try
            {
                if (ToTimestamp(AntigravityLogParser.ParseTrajectoryTimestamp(
                        reader.GetFieldValue<byte[]>(0))) is { } timestamp)
                {
                    return timestamp;
                }
            }
            catch (FormatException)
            {
            }
        }
        return null;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void AddUsage(
        List<ParsedEvent> output,
        Dictionary<string, (DateTimeOffset Timestamp, int Rank)> identityTimestamps,
        AntigravityLogParser.Usage? usage,
        string? contextModel,
        long? contextProvider,
        long? ownTimestampMilliseconds,
        DateTimeOffset? trajectoryTimestamp,
        DateTimeOffset fallbackTimestamp,
        string sessionId)
    {
        if (usage is null || !usage.HasTokens) return;

        var identities = usage.IdentityKeys().ToList();
        var timestamp = ToTimestamp(ownTimestampMilliseconds);
        var timestampRank = timestamp is not null ? 3 : 0;
        if (timestamp is null)
        {
            foreach (var identity in identities)
            {
                if (identityTimestamps.TryGetValue(identity, out var previous))
                {
                    timestamp = previous.Timestamp;
                    timestampRank = previous.Rank;
                    break;
                }
            }
        }
        timestamp ??= trajectoryTimestamp;
        if (timestamp is not null && timestampRank == 0) timestampRank = 1;
        var resolvedTimestamp = timestamp ?? fallbackTimestamp;

        var model = usage.ModelId is > 0
            ? AntigravityLogParser.NormalizeModel(null, usage.ModelId)
            : AntigravityLogParser.NormalizeModel(contextModel, null);
        // TokenEvent has one output bucket; fold AGY's reasoning tokens into
        // it so wire totals and the existing report cards do not undercount
        // generations whose visible answer is only part of the output.
        var totalOutput = AntigravityLogParser.TotalOutputTokens(usage);
        var tokenEvent = new TokenEvent(
            TriggerTool.Antigravity,
            resolvedTimestamp,
            model,
            usage.InputTokens,
            totalOutput,
            usage.CacheCreationTokens,
            usage.CacheReadTokens);
        output.Add(new ParsedEvent(tokenEvent, identities, timestampRank, sessionId, usage.PreferredMessageId));

        foreach (var identity in identities)
        {
            if (!identityTimestamps.TryGetValue(identity, out var previous)
                || timestampRank > previous.Rank
                || resolvedTimestamp < previous.Timestamp)
            {
                identityTimestamps[identity] = (resolvedTimestamp, timestampRank);
            }
        }
    }

    private static List<ParsedEvent> MergeByIdentity(List<ParsedEvent> events)
    {
        var slots = new List<ParsedEvent?>();
        var identityIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            var matches = item.Identities
                .Where(identityIndexes.ContainsKey)
                .Select(identity => identityIndexes[identity])
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            if (matches.Length == 0)
            {
                var index = slots.Count;
                slots.Add(item);
                foreach (var identity in item.Identities) identityIndexes[identity] = index;
                continue;
            }

            var targetIndex = matches[0];
            var target = slots[targetIndex]!;
            foreach (var duplicateIndex in matches.Skip(1))
            {
                if (slots[duplicateIndex] is { } duplicate)
                {
                    MergeInto(target, duplicate);
                    slots[duplicateIndex] = null;
                }
            }
            MergeInto(target, item);
            foreach (var identity in target.Identities) identityIndexes[identity] = targetIndex;
        }
        return slots.OfType<ParsedEvent>().ToList();
    }

    private static void MergeInto(ParsedEvent target, ParsedEvent duplicate)
    {
        var current = target.Event;
        var incoming = duplicate.Event;
        var model = current.Model == "gemini-internal-model" && incoming.Model != current.Model
            ? incoming.Model
            : current.Model;
        var useIncomingTimestamp = duplicate.TimestampRank > target.TimestampRank
            || duplicate.TimestampRank == target.TimestampRank && incoming.Timestamp < current.Timestamp;
        var timestamp = useIncomingTimestamp ? incoming.Timestamp : current.Timestamp;
        target.Event = new TokenEvent(
            TriggerTool.Antigravity,
            timestamp,
            model,
            Math.Max(current.InputTokens, incoming.InputTokens),
            Math.Max(current.OutputTokens, incoming.OutputTokens),
            Math.Max(current.CacheCreationTokens, incoming.CacheCreationTokens),
            Math.Max(current.CacheReadTokens, incoming.CacheReadTokens));
        if (useIncomingTimestamp) target.TimestampRank = duplicate.TimestampRank;
        if (string.IsNullOrWhiteSpace(target.MessageId) && !string.IsNullOrWhiteSpace(duplicate.MessageId))
            target.MessageId = duplicate.MessageId;
        foreach (var identity in duplicate.Identities)
        {
            if (!target.Identities.Contains(identity, StringComparer.Ordinal)) target.Identities.Add(identity);
        }
    }

    private static List<TokenEvent> DeduplicateCrossRoot(IReadOnlyList<TokenEvent> events)
    {
        var seen = new HashSet<DedupKey>();
        var output = new List<TokenEvent>(events.Count);
        foreach (var tokenEvent in events)
        {
            var key = new DedupKey(
                tokenEvent.Timestamp.ToUnixTimeMilliseconds(),
                tokenEvent.Model,
                tokenEvent.InputTokens,
                tokenEvent.OutputTokens,
                tokenEvent.CacheCreationTokens,
                tokenEvent.CacheReadTokens);
            if (seen.Add(key)) output.Add(tokenEvent);
        }
        return output;
    }

    private static string? ContextModel(string? raw, long? modelId) => raw is not null
        ? AntigravityLogParser.NormalizeModel(raw)
        : modelId is > 0
            ? AntigravityLogParser.NormalizeModel(null, modelId)
            : null;

    private static DateTimeOffset FileTimestamp(string path)
    {
        try
        {
            var timestamp = File.GetLastWriteTimeUtc(path);
            return timestamp.Year >= 1700 ? new DateTimeOffset(timestamp) : DateTimeOffset.UnixEpoch;
        }
        catch
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static DateTimeOffset? ToTimestamp(long? milliseconds)
    {
        if (milliseconds is not { } value
            || value < -62_135_596_800_000L
            || value > 253_402_300_799_999L)
        {
            return null;
        }
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ParsedEvent(
        TokenEvent tokenEvent,
        IEnumerable<string> identities,
        int timestampRank,
        string sessionId,
        string? messageId)
    {
        public TokenEvent Event { get; set; } = tokenEvent;
        public List<string> Identities { get; } = identities.ToList();
        public int TimestampRank { get; set; } = timestampRank;
        public string SessionId { get; } = sessionId;
        public string? MessageId { get; set; } = messageId;
    }

    private readonly record struct DedupKey(
        long Timestamp,
        string Model,
        long Input,
        long Output,
        long CacheCreation,
        long CacheRead);
}
