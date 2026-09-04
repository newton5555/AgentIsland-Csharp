using System.Net.Http;
using AgentIsland.Providers.Usage.DeepSeek;

namespace AgentIsland.Backend.Usage;

/// Queries DeepSeek's official account-balance endpoint for the API key used
/// by Harness. This is intentionally not part of UsageFetcher: the endpoint
/// returns currency balances, not a percentage quota window.
public static class DeepSeekBalanceFetcher
{
    public const string BalanceEndpoint = "https://api.deepseek.com/user/balance";

    public abstract record Outcome
    {
        public sealed record Success(DeepSeekBalanceSnapshot Snapshot) : Outcome;
        public sealed record NotConfigured : Outcome;
        public sealed record Unauthorized : Outcome;
        public sealed record Failed(string Message) : Outcome;
    }

    public static async Task<Outcome> Fetch(CancellationToken ct = default)
    {
        var apiKey = DeepSeekCredentials.ReadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return new Outcome.NotConfigured();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            // Keep this provider probe bounded independently of the shared
            // client: a dead network must not hold the island refresh forever.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Http.Client.SendAsync(request, deadline.Token);
            var status = (int)response.StatusCode;
            if (status is 401 or 403) return new Outcome.Unauthorized();
            if (status != 200) return new Outcome.Failed($"http {status}");

            var snapshot = DeepSeekBalanceParser.Parse(
                await response.Content.ReadAsByteArrayAsync(deadline.Token));
            return snapshot is null
                ? new Outcome.Failed("parse error")
                : new Outcome.Success(snapshot);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Outcome.Failed("network timeout");
        }
        catch (Exception error)
        {
            return new Outcome.Failed(
                error is HttpRequestException ? "network drop" : error.Message);
        }
    }
}
