using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Usage;
using AgentIsland.Providers.Usage.DeepSeek;

namespace AgentIsland.Runtime.Snapshots;

/// <summary>
/// Snapshot source querying DeepSeek's official account-balance endpoint.
///
/// Rules:
/// 1. Fixed endpoint: https://api.deepseek.com/user/balance. Strictly official only.
/// 2. Resolves API key via DEEPSEEK_API_KEY environment variable or ~/.dsh/.credentials.yaml.
/// 3. Never writes API key or secrets to logs, cache, or error messages.
/// 4. Maps unconfigured key to NotConfigured; 401/403, timeouts, network drops, and parse errors to Error.
/// 5. Respects external cancellation without swallowing OperationCanceledException.
/// 6. Decoupled from WPF/Windows.
/// </summary>
public sealed class DeepSeekBalanceSnapshotSource : IAgentSnapshotSource
{
    public const string Endpoint = "https://api.deepseek.com/user/balance";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient SharedHttpClient = new();

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly TimeSpan _timeout;

    public AgentKey Agent { get; }

    public DeepSeekBalanceSnapshotSource(
        AgentKey? agentKey = null,
        HttpClient? httpClient = null,
        Func<string?>? apiKeyProvider = null,
        TimeSpan? timeout = null,
        string? credentialsFilePath = null)
    {
        Agent = agentKey ?? (AgentKey)"deepseek";
        _httpClient = httpClient ?? SharedHttpClient;
        _apiKeyProvider = apiKeyProvider ?? (() => ReadDefaultApiKey(credentialsFilePath));
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<AgentSnapshot> ReadAsync(DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? apiKey;
        try
        {
            apiKey = _apiKeyProvider();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentSnapshot(
                Agent,
                ActivityState.Idle,
                SnapshotAvailability.Error,
                observedAt,
                DataAt: null,
                Error: "credentials read failure: " + ex.GetType().Name);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AgentSnapshot(
                Agent,
                ActivityState.Idle,
                SnapshotAvailability.NotConfigured,
                observedAt);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new AgentSnapshot(
                    Agent,
                    ActivityState.Idle,
                    SnapshotAvailability.Error,
                    observedAt,
                    DataAt: null,
                    Error: "network timeout");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 401 or 403)
                {
                    return new AgentSnapshot(
                        Agent,
                        ActivityState.Idle,
                        SnapshotAvailability.Error,
                        observedAt,
                        DataAt: null,
                        Error: "unauthorized");
                }

                if (status != 200)
                {
                    return new AgentSnapshot(
                        Agent,
                        ActivityState.Idle,
                        SnapshotAvailability.Error,
                        observedAt,
                        DataAt: null,
                        Error: $"http {status}");
                }

                byte[] data;
                try
                {
                    data = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new AgentSnapshot(
                        Agent,
                        ActivityState.Idle,
                        SnapshotAvailability.Error,
                        observedAt,
                        DataAt: null,
                        Error: "network timeout");
                }

                var snapshot = DeepSeekBalanceParser.Parse(data);
                if (snapshot is null)
                {
                    return new AgentSnapshot(
                        Agent,
                        ActivityState.Idle,
                        SnapshotAvailability.Error,
                        observedAt,
                        DataAt: null,
                        Error: "parse error");
                }

                var entries = snapshot.Balances
                    .Select(b => new AccountBalanceEntry(b.Currency, b.TotalBalance, b.GrantedBalance, b.ToppedUpBalance))
                    .ToArray();

                var coreBalance = new AccountBalanceSnapshot(snapshot.IsAvailable, entries);

                return new AgentSnapshot(
                    Agent,
                    ActivityState.Idle,
                    SnapshotAvailability.Ready,
                    observedAt,
                    observedAt,
                    Balance: coreBalance);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var msg = error is HttpRequestException ? "network drop" : error.Message;
            return new AgentSnapshot(
                Agent,
                ActivityState.Idle,
                SnapshotAvailability.Error,
                observedAt,
                DataAt: null,
                Error: msg);
        }
    }

    public static string DefaultCredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dsh",
        ".credentials.yaml");

    public static string? ReadDefaultApiKey(string? credentialsPath = null)
    {
        var env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var path = credentialsPath ?? DefaultCredentialsPath;
        try
        {
            if (File.Exists(path))
            {
                return ParseApiKeyFromYaml(File.ReadAllText(path));
            }
        }
        catch
        {
            // Do not log secret or fail silently
        }

        return null;
    }

    internal static string? ParseApiKeyFromYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        var inRefs = false;
        var refsIndent = -1;
        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var content = StripComment(line);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var indent = content.TakeWhile(char.IsWhiteSpace).Count();
            var trimmed = content.Trim();
            if (!inRefs)
            {
                if (trimmed.Equals("refs:", StringComparison.OrdinalIgnoreCase))
                {
                    inRefs = true;
                    refsIndent = indent;
                }
                continue;
            }

            if (indent <= refsIndent && !trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                inRefs = trimmed.Equals("refs:", StringComparison.OrdinalIgnoreCase);
                if (!inRefs) break;
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            var key = trimmed[..colon].Trim();
            if (!key.Equals("DEEPSEEK_API_KEY", StringComparison.OrdinalIgnoreCase)) continue;
            return ResolveScalar(trimmed[(colon + 1)..].Trim());
        }
        return null;
    }

    private static string? ResolveScalar(string raw)
    {
        if (raw.Length == 0 || raw is "~" or "null" or "NULL") return null;

        if (raw.StartsWith("${", StringComparison.Ordinal) && raw.EndsWith('}'))
        {
            return Environment.GetEnvironmentVariable(raw[2..^1]);
        }
        if (raw.Length > 0 && raw[0] == '$')
        {
            return Environment.GetEnvironmentVariable(raw[1..]);
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(raw); }
            catch { return raw[1..^1]; }
        }
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        return raw.Trim();
    }

    private static string StripComment(string line)
    {
        var single = false;
        var doubleQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !doubleQuote) single = !single;
            else if (c == '"' && !single && (i == 0 || line[i - 1] != '\\')) doubleQuote = !doubleQuote;
            else if (c == '#' && !single && !doubleQuote && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }
        return line;
    }
}
