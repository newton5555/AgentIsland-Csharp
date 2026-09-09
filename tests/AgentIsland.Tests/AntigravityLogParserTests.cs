using System.Text;
using Microsoft.Data.Sqlite;
using AgentIsland.Providers.Cost.Antigravity;

namespace AgentIsland.Tests;

public sealed class AntigravityLogParserTests
{
    [Fact]
    public void GeneratorMetadataDecodesTokenBucketsAndTimestamp()
    {
        var usage = UsageBlob(
            modelId: 313,
            input: 100,
            totalOutput: 60,
            cacheCreation: 10,
            cacheRead: 20,
            reasoning: 15,
            visibleOutput: 45,
            messageId: "message-1",
            responseId: "response-1");
        var timestamp = TimestampBlob(1_700_000_000, 250_000_000);
        var generationInfo = BytesField(4, timestamp);
        var chatModel = VarintField(3, 313)
            .Concat(BytesField(4, usage))
            .Concat(BytesField(9, generationInfo))
            .Concat(BytesField(21, "Gemini 3 Flash"u8.ToArray()))
            .ToArray();
        var metadata = AntigravityLogParser.ParseGeneratorMetadata(
            BytesField(1, chatModel));

        Assert.Equal("Gemini 3 Flash", metadata.Model);
        Assert.Equal(313, metadata.ModelId);
        Assert.Equal(1_700_000_000_250, metadata.TimestampUnixMilliseconds);
        Assert.NotNull(metadata.Usage);
        Assert.Equal(100, metadata.Usage!.InputTokens);
        Assert.Equal(60, metadata.Usage.TotalOutputTokens);
        Assert.Equal(10, metadata.Usage.CacheCreationTokens);
        Assert.Equal(20, metadata.Usage.CacheReadTokens);
        Assert.Equal(15, metadata.Usage.ReasoningTokens);
        Assert.Equal(45, metadata.Usage.VisibleOutputTokens);
        Assert.Equal("response-1", metadata.Usage.ResponseId);
        Assert.Equal(60, AntigravityLogParser.TotalOutputTokens(metadata.Usage));
    }

    [Theory]
    [InlineData("Gemini 3 Flash (Preview)", "gemini-3.6-flash")]
    [InlineData("gemini-3-flash-c", "gemini-3-flash-preview")]
    [InlineData("MODEL_PLACEHOLDER_M26", "claude-opus-4-6")]
    [InlineData("Claude 3.5 Sonnet", "claude-3-5-sonnet")]
    public void ModelAliasesAreNormalized(string raw, string expected)
    {
        Assert.Equal(expected, AntigravityLogParser.NormalizeModel(raw));
    }

    [Fact]
    public void SqliteReaderCombinesGenerationAndStepCopiesOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-island-agy-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB);
                    CREATE TABLE steps (idx INTEGER PRIMARY KEY, metadata BLOB);
                    CREATE TABLE trajectory_metadata_blob (id TEXT PRIMARY KEY, data BLOB);
                    """;
                create.ExecuteNonQuery();

                var usage = UsageBlob(
                    modelId: 313,
                    input: 100,
                    totalOutput: 60,
                    visibleOutput: 45,
                    reasoning: 15,
                    responseId: "response-duplicate");
                var timestamp = TimestampBlob(1_700_000_000, 0);
                var generationInfo = BytesField(4, timestamp);
                var generator = VarintField(3, 313)
                    .Concat(BytesField(4, usage))
                    .Concat(BytesField(9, generationInfo))
                    .ToArray();
                Insert(connection, "gen_metadata", 0, BytesField(1, generator));

                var modelInfo = VarintField(1, 313).Concat(BytesField(12, "gemini 3 flash"u8.ToArray())).ToArray();
                var step = BytesField(9, usage)
                    .Concat(BytesField(24, modelInfo))
                    .Concat(BytesField(8, timestamp))
                    .ToArray();
                Insert(connection, "steps", 0, step, "metadata");
            }

            var events = AntigravityLogReader.ParseFile(path);

            var tokenEvent = Assert.Single(events);
            Assert.Equal(100, tokenEvent.InputTokens);
            Assert.Equal(60, tokenEvent.OutputTokens);
            Assert.Equal("gemini-2.5-flash-thinking", tokenEvent.Model);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void Insert(SqliteConnection connection, string table, long index, byte[] blob, string column = "data")
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table} (idx, {column}) VALUES ($idx, $blob)";
        command.Parameters.AddWithValue("$idx", index);
        command.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
        command.ExecuteNonQuery();
    }

    private static byte[] UsageBlob(
        ulong modelId,
        ulong input,
        ulong totalOutput,
        ulong cacheCreation = 0,
        ulong cacheRead = 0,
        ulong reasoning = 0,
        ulong visibleOutput = 0,
        string? messageId = null,
        string? responseId = null)
    {
        var bytes = VarintField(1, modelId)
            .Concat(VarintField(2, input))
            .Concat(VarintField(3, totalOutput))
            .Concat(VarintField(4, cacheCreation))
            .Concat(VarintField(5, cacheRead))
            .Concat(VarintField(9, reasoning))
            .Concat(VarintField(10, visibleOutput));
        if (messageId is not null) bytes = bytes.Concat(BytesField(7, Encoding.UTF8.GetBytes(messageId)));
        if (responseId is not null) bytes = bytes.Concat(BytesField(11, Encoding.UTF8.GetBytes(responseId)));
        return bytes.ToArray();
    }

    private static byte[] TimestampBlob(ulong seconds, ulong nanos) =>
        VarintField(1, seconds).Concat(VarintField(2, nanos)).ToArray();

    private static byte[] VarintField(ulong number, ulong value)
    {
        var output = new List<byte>();
        WriteVarint((number << 3), output);
        WriteVarint(value, output);
        return output.ToArray();
    }

    private static byte[] BytesField(ulong number, byte[] value)
    {
        var output = new List<byte>();
        WriteVarint((number << 3) | 2, output);
        WriteVarint((ulong)value.Length, output);
        output.AddRange(value);
        return output.ToArray();
    }

    private static void WriteVarint(ulong value, List<byte> output)
    {
        while (value >= 0x80)
        {
            output.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }
}
