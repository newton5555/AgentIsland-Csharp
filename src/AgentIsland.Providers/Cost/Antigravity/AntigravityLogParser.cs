using System.Text;
using AgentIsland.Core;
using AgentIsland.Core.Cost;

namespace AgentIsland.Providers.Cost.Antigravity;

/// Decodes the small protobuf messages stored in Antigravity conversation
/// databases. The database reader stays in the Windows host; this type owns
/// only the source-specific wire format and model/token mapping so it can be
/// exercised without a live Antigravity installation.
///
/// Antigravity does not ship a public .proto contract for these rows. The
/// field numbers mirror the format used by current ccusage/tokscale readers:
/// gen_metadata contains a chat-model envelope, while newer databases also
/// expose the same usage in steps metadata (including retry attempts).
public static class AntigravityLogParser
{
    private const string FallbackModel = "gemini-internal-model";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public sealed record GeneratorMetadata(
        string? Model,
        long? ModelId,
        Usage? Usage,
        IReadOnlyList<Usage> RetryUsages,
        long? TimestampUnixMilliseconds);

    public sealed record StepMetadata(
        string? Model,
        long? ModelId,
        long? Provider,
        Usage? Usage,
        IReadOnlyList<Usage> RetryUsages,
        long? TimestampUnixMilliseconds);

    public sealed record Usage(
        long? ModelId,
        long InputTokens,
        long TotalOutputTokens,
        long CacheCreationTokens,
        long CacheReadTokens,
        long ReasoningTokens,
        long VisibleOutputTokens,
        long? Provider,
        string? MessageId,
        string? ResponseId,
        string? ProviderAssignedMessageId)
    {
        public bool HasTokens => InputTokens > 0
            || TotalOutputTokens > 0
            || CacheCreationTokens > 0
            || CacheReadTokens > 0
            || ReasoningTokens > 0
            || VisibleOutputTokens > 0;

        public IReadOnlyList<string> IdentityKeys()
        {
            var keys = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(ResponseId)) keys.Add("response:" + ResponseId);
            if (!string.IsNullOrWhiteSpace(ProviderAssignedMessageId))
                keys.Add("provider:" + ProviderAssignedMessageId);
            if (!string.IsNullOrWhiteSpace(MessageId)) keys.Add("message:" + MessageId);
            return keys;
        }

        public string? PreferredMessageId => !string.IsNullOrWhiteSpace(ResponseId)
            ? ResponseId
            : !string.IsNullOrWhiteSpace(ProviderAssignedMessageId)
                ? ProviderAssignedMessageId
                : MessageId;
    }

    public static GeneratorMetadata ParseGeneratorMetadata(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var root = DecodeFields(blob);
        var chatModel = FieldBytes(root, 1)
            ?? throw new FormatException("Antigravity generator metadata has no chat model envelope.");
        var fields = DecodeFields(chatModel);
        var usage = FieldBytes(fields, 4) is { } usageBlob
            ? ParseModelUsage(usageBlob)
            : null;
        var retries = FieldBytesAll(fields, 17)
            .Select(ParseRetryInfo)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        var timestamp = FieldBytes(fields, 9) is { } generationInfo
            ? ParseGenerationInfoTimestamp(generationInfo)
            : null;

        return new GeneratorMetadata(
            FieldText(fields, 19) ?? FieldText(fields, 21),
            PositiveLong(FieldVarint(fields, 3)),
            usage,
            retries,
            timestamp);
    }

    public static StepMetadata ParseStepMetadata(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var fields = DecodeFields(blob);
        var usage = FieldBytes(fields, 9) is { } usageBlob
            ? ParseModelUsage(usageBlob)
            : null;
        var retries = FieldBytesAll(fields, 28)
            .Select(ParseRetryInfo)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        var modelInfo = FieldBytes(fields, 24) is { } modelInfoBlob
            ? ParseModelInfo(modelInfoBlob)
            : new ModelInfo(null, null, null);
        var timestampBlob = FieldBytes(fields, 8) ?? FieldBytes(fields, 1);
        var timestamp = timestampBlob is { } timestampValue
            ? ParseTimestampMessage(timestampValue)
            : null;

        return new StepMetadata(
            modelInfo.Model,
            modelInfo.ModelId,
            modelInfo.Provider,
            usage,
            retries,
            timestamp);
    }

    public static long? ParseTrajectoryTimestamp(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var fields = DecodeFields(blob);
        return FieldBytes(fields, 2) is { } timestamp
            ? ParseTimestampMessage(timestamp)
            : null;
    }

    public static string ModelNameFromId(long modelId) => modelId switch
    {
        246 => "gemini-2.5-pro",
        312 => "gemini-2.5-flash",
        313 or 329 => "gemini-2.5-flash-thinking",
        330 => "gemini-2.5-flash-lite",
        281 or 282 => "claude-4-sonnet",
        290 or 291 => "claude-4-opus",
        333 or 334 => "claude-4.5-sonnet",
        340 or 341 => "claude-4.5-haiku",
        342 => "model_openai_gpt_oss_120b_medium",
        >= 1000 => "model_placeholder_m" + (modelId - 1000),
        _ => "antigravity-model-" + modelId,
    };

    public static string NormalizeModel(string? raw, long? modelId = null)
    {
        if (modelId is > 0) raw ??= ModelNameFromId(modelId.Value);
        if (string.IsNullOrWhiteSpace(raw)) return FallbackModel;

        var trimmed = raw.Trim();
        var lower = trimmed.ToLowerInvariant();
        var baseName = lower.IndexOf('(') is var paren && paren >= 0
            ? lower[..paren].Trim()
            : lower;
        var normalized = baseName switch
        {
            "gemini 3.7 flash" or "gemini 3.7 flash thinking" => "gemini-3.7-flash",
            "gemini 3.7 pro" or "gemini 3.7 pro thinking" => "gemini-3.7-pro",
            "gemini 3.6 flash" or "gemini 3 flash" => "gemini-3.6-flash",
            "gemini 3.6 pro" => "gemini-3.6-pro",
            "gemini 3 pro" or "gemini 3 pro thinking" => "gemini-3-pro",
            "gemini 2.5 flash" => "gemini-2.5-flash",
            "gemini 2.5 pro" => "gemini-2.5-pro",
            "gemini 2.0 flash" or "gemini 2 flash" => "gemini-2.0-flash",
            "gemini 2.0 pro" => "gemini-2.0-pro",
            "gemini 1.5 flash" => "gemini-1.5-flash",
            "gemini 1.5 pro" => "gemini-1.5-pro",
            "model_placeholder_m26" => "claude-opus-4-6",
            "model_placeholder_m35" => "claude-sonnet-4-6",
            "model_placeholder_m16" or "model_placeholder_m36" or "model_placeholder_m37" => "gemini-3.1-pro",
            "model_placeholder_m18" or "model_placeholder_m47" or "model_placeholder_m84" => "gemini-3-flash-preview",
            "model_placeholder_m20" => "gemini-3.5-flash-medium",
            "model_placeholder_m132" or "model_placeholder_m133" => "gemini-3.5-flash-high",
            "model_placeholder_m187" => "gemini-3.5-flash-extra-low",
            "model_openai_gpt_oss_120b_medium" => "gpt-oss-120b-medium",
            "gemini-pro-default" or "gemini-pro-agent" => "gemini-3.1-pro",
            "gemini-3-flash-agent" or "gemini-3-flash-agent-a" or "gemini-3-flash-agent-b"
                or "gemini-3-flash-a" or "gemini-3-flash-b" => "gemini-3.5-flash-high",
            "gemini-3-flash-c" or "gemini-3-flash" => "gemini-3-flash-preview",
            "gemini-3.5-flash-low" => "gemini-3.5-flash-medium",
            "gemini-3.1-pro-high" or "gemini-3.1-pro-low" => "gemini-3.1-pro",
            "gemini-3-pro-high" or "gemini-3-pro-low" => "gemini-3-pro",
            "claude 3.7 sonnet" or "claude 3.7 sonnet thinking" => "claude-3-7-sonnet",
            "claude 3.5 sonnet" => "claude-3-5-sonnet",
            "claude 3.5 haiku" => "claude-3-5-haiku",
            "claude 3 opus" => "claude-3-opus",
            _ => baseName.Replace(' ', '-'),
        };
        return string.IsNullOrWhiteSpace(normalized) ? FallbackModel : normalized;
    }

    public static long TotalOutputTokens(Usage usage) => Math.Max(
        usage.TotalOutputTokens,
        SaturatingAdd(usage.VisibleOutputTokens, usage.ReasoningTokens));

    private static Usage ParseModelUsage(ReadOnlyMemory<byte> blob)
    {
        var fields = DecodeFields(blob);
        return new Usage(
            PositiveLong(FieldVarint(fields, 1)),
            NonNegativeLong(FieldVarint(fields, 2)),
            NonNegativeLong(FieldVarint(fields, 3)),
            NonNegativeLong(FieldVarint(fields, 4)),
            NonNegativeLong(FieldVarint(fields, 5)),
            NonNegativeLong(FieldVarint(fields, 9)),
            NonNegativeLong(FieldVarint(fields, 10)),
            PositiveLong(FieldVarint(fields, 6)),
            FieldText(fields, 7),
            FieldText(fields, 11),
            FieldText(fields, 12));
    }

    private static Usage? ParseRetryInfo(ReadOnlyMemory<byte> blob)
    {
        var fields = DecodeFields(blob);
        return FieldBytes(fields, 2) is { } usage ? ParseModelUsage(usage) : null;
    }

    private static ModelInfo ParseModelInfo(ReadOnlyMemory<byte> blob)
    {
        var fields = DecodeFields(blob);
        return new ModelInfo(
            FieldText(fields, 12) ?? FieldText(fields, 8),
            PositiveLong(FieldVarint(fields, 1)),
            PositiveLong(FieldVarint(fields, 7)));
    }

    private static long? ParseGenerationInfoTimestamp(ReadOnlyMemory<byte> blob)
    {
        var fields = DecodeFields(blob);
        return FieldBytes(fields, 4) is { } timestamp
            ? ParseTimestampMessage(timestamp)
            : null;
    }

    private static long? ParseTimestampMessage(ReadOnlyMemory<byte> blob)
    {
        var fields = DecodeFields(blob);
        var seconds = FieldVarint(fields, 1);
        if (seconds is not > 0 || seconds > 253_402_300_799) return null;
        var nanos = Math.Min(FieldVarint(fields, 2) ?? 0, 999_999_999UL);
        return checked((long)seconds.Value * 1000L + (long)(nanos / 1_000_000));
    }

    private static List<ProtoField> DecodeFields(ReadOnlyMemory<byte> blob)
    {
        var output = new List<ProtoField>();
        var span = blob.Span;
        var offset = 0;
        while (offset < span.Length)
        {
            var tag = ReadVarint(span, ref offset);
            var number = tag >> 3;
            if (number == 0 || number > uint.MaxValue)
                throw new FormatException("Invalid Antigravity protobuf field number.");

            switch (tag & 7)
            {
                case 0:
                    output.Add(new ProtoField((int)number, WireKind.Varint, ReadVarint(span, ref offset), default));
                    break;
                case 1:
                    EnsureRemaining(span, offset, 8);
                    offset += 8;
                    output.Add(new ProtoField((int)number, WireKind.Fixed64, 0, default));
                    break;
                case 2:
                {
                    var length = ReadVarint(span, ref offset);
                    if (length > int.MaxValue) throw new FormatException("Antigravity protobuf length overflow.");
                    EnsureRemaining(span, offset, (int)length);
                    output.Add(new ProtoField(
                        (int)number,
                        WireKind.Bytes,
                        0,
                        blob.Slice(offset, (int)length)));
                    offset += (int)length;
                    break;
                }
                case 5:
                    EnsureRemaining(span, offset, 4);
                    offset += 4;
                    output.Add(new ProtoField((int)number, WireKind.Fixed32, 0, default));
                    break;
                default:
                    throw new FormatException("Unsupported Antigravity protobuf wire type.");
            }
        }
        return output;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int offset)
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            if (offset >= span.Length) throw new FormatException("Truncated Antigravity protobuf varint.");
            var current = span[offset++];
            var payload = (ulong)(current & 0x7f);
            if (index == 9 && payload > 1) throw new FormatException("Antigravity protobuf varint overflow.");
            value |= payload << (index * 7);
            if ((current & 0x80) == 0) return value;
        }
        throw new FormatException("Antigravity protobuf varint overflow.");
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> span, int offset, int length)
    {
        if (length < 0 || offset > span.Length - length)
            throw new FormatException("Truncated Antigravity protobuf value.");
    }

    private static ulong? FieldVarint(IReadOnlyList<ProtoField> fields, int number)
    {
        foreach (var field in fields.Reverse())
        {
            if (field.Number == number && field.Kind == WireKind.Varint) return field.Varint;
        }
        return null;
    }

    private static ReadOnlyMemory<byte>? FieldBytes(IReadOnlyList<ProtoField> fields, int number)
    {
        foreach (var field in fields)
        {
            if (field.Number == number && field.Kind == WireKind.Bytes) return field.Bytes;
        }
        return null;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> FieldBytesAll(
        IReadOnlyList<ProtoField> fields,
        int number) => fields
        .Where(field => field.Number == number && field.Kind == WireKind.Bytes)
        .Select(field => field.Bytes);

    private static string? FieldText(IReadOnlyList<ProtoField> fields, int number)
    {
        foreach (var field in fields.Reverse())
        {
            if (field.Number != number || field.Kind != WireKind.Bytes) continue;
            try
            {
                var value = StrictUtf8.GetString(field.Bytes.Span).Trim();
                if (value.Length > 0) return value;
            }
            catch (DecoderFallbackException)
            {
                // This field may be another nested message in a newer schema.
            }
        }
        return null;
    }

    private static long NonNegativeLong(ulong? value) => value is null
        ? 0
        : value.Value > long.MaxValue ? long.MaxValue : (long)value.Value;

    private static long? PositiveLong(ulong? value) => value is > 0
        ? value.Value > long.MaxValue ? long.MaxValue : (long)value.Value
        : null;

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right
        ? long.MaxValue
        : left + right;

    private readonly record struct ModelInfo(string? Model, long? ModelId, long? Provider);
    private readonly record struct ProtoField(int Number, WireKind Kind, ulong Varint, ReadOnlyMemory<byte> Bytes);

    private enum WireKind
    {
        Varint,
        Fixed64,
        Bytes,
        Fixed32,
    }
}
