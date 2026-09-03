using System.IO;
using System.Net.Http;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

public static class UsageFetcher
{
    // MARK: - Codex

    /// Codex usage lives at chatgpt.com/backend-api/wham/usage and accepts
    /// the access_token from ~/.codex/auth.json. The endpoint is reliable
    /// and rarely rate-limited, so this is the easy half of the integration.
    public static async Task<AppUsage> FetchCodex(CancellationToken ct = default)
    {
        if (ReadCodexAccessToken() is not { } token)
        {
            return AppUsage.ErrorPair("no codex auth");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await Http.Client.SendAsync(request, ct);
            var status = (int)response.StatusCode;

            // 401 means the access_token has expired. The Codex CLI rotates
            // this token on its own — surface the exact remediation step.
            if (status == 401) return AppUsage.ErrorPair("auth expired — codex login");
            if (status != 200) return AppUsage.ErrorPair($"http {status}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            // The payload gained credits/spend_control alongside the newer
            // multi-window shape; only the fields below are read, so any
            // additions are ignored by construction.
            if (Jsonl.GetObject(doc.RootElement, "rate_limit") is not { } rateLimit)
            {
                return AppUsage.ErrorPair("parse error");
            }

            // Banked rate-limit resets ("reset cards") — the escape hatches
            // of the weekly-only era. Count from the usage payload; per-card
            // detail from its own endpoint, best-effort (null keeps the
            // count-only display).
            int? resetCards = null;
            if (Jsonl.GetObject(doc.RootElement, "rate_limit_reset_credits") is { } credits
                && credits.TryGetProperty("available_count", out var available)
                && available.ValueKind == JsonValueKind.Number)
            {
                resetCards = available.GetInt32();
            }
            var details = await FetchResetCardDetails(token, ct);
            resetCards ??= details?.Count;

            return new AppUsage(
                ParseCodexWindow(Jsonl.GetObject(rateLimit, "primary_window")),
                ParseCodexWindow(Jsonl.GetObject(rateLimit, "secondary_window")),
                Jsonl.GetString(doc.RootElement, "plan_type"),
                resetCards,
                details);
        }
        catch (Exception error)
        {
            return AppUsage.ErrorPair(error.Message);
        }
    }

    /// GET wham/rate-limit-reset-credits → the available cards, each with
    /// OpenAI's own title ("Full reset") and expires_at. Returns null on any
    /// failure so the caller keeps the count-only display.
    private static async Task<IReadOnlyList<ResetCard>?> FetchResetCardDetails(
        string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await Http.Client.SendAsync(request, ct);
            if ((int)response.StatusCode != 200) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            if (!doc.RootElement.TryGetProperty("credits", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var cards = new List<ResetCard>();
            foreach (var row in rows.EnumerateArray())
            {
                if (Jsonl.GetString(row, "status") != "available") continue;
                if (Jsonl.GetString(row, "id") is not { } id) continue;
                var title = Jsonl.GetString(row, "title") ?? "Reset";
                DateTimeOffset? expires = Jsonl.GetString(row, "expires_at") is { } iso
                    && DateTimeOffset.TryParse(iso, out var parsed) ? parsed : null;
                cards.Add(new ResetCard(id, title, expires));
            }
            return cards;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadCodexAccessToken()
    {
        try
        {
            var path = IslandPaths.CodexAuthFile;
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (Jsonl.GetObject(doc.RootElement, "tokens") is not { } tokens) return null;
            return Jsonl.GetString(tokens, "access_token");
        }
        catch
        {
            return null;
        }
    }

    internal static WindowUsage ParseCodexWindow(JsonElement? obj)
    {
        if (obj is not { } window) return WindowUsage.Unknown;
        var used = Jsonl.GetDouble(window, "used_percent") ?? 0;
        DateTimeOffset? resetAt = Jsonl.GetDouble(window, "reset_at") is { } epoch
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000))
            : null;
        // The payload states its own window length (e.g. 18000s or 604800s). Labels render from it.
        var period = Jsonl.GetDouble(window, "limit_window_seconds");
        return new WindowUsage(used / 100, resetAt, null, period);
    }

    // MARK: - Claude

    /// Anthropic doesn't ship a usage endpoint for end users — Claude Code
    /// itself talks to api.anthropic.com/api/oauth/usage with a beta header
    /// and a User-Agent that identifies as the CLI. We replicate that.
    public static async Task<AppUsage> FetchClaude(CancellationToken ct = default)
    {
        // Nothing here may throw: a fetch that faults kills the refresh
        // task before it can clear UsageStore.Loading, freezing usage (and
        // auto-resume) until an app relaunch. Credential file IO races with
        // the CLI rewriting the file are a real occurrence, not a theory.
        try
        {
            var resolution = await ClaudeCredentials.ResolveUsage((token, plan) => FetchClaudeUsage(token, plan, ct));
            return resolution switch
            {
                ClaudeCredentials.Resolution.Usage usage => usage.Value,
                ClaudeCredentials.Resolution.ReauthRequired reauth => AppUsage.ErrorPair(reauth.Message),
                ClaudeCredentials.Resolution.Failed failed => AppUsage.ErrorPair(failed.Message),
                _ => AppUsage.ErrorPair("bad response"),
            };
        }
        catch (Exception error)
        {
            return AppUsage.ErrorPair(error.Message);
        }
    }

    private static async Task<ClaudeCredentials.ProbeOutcome> FetchClaudeUsage(string token, string? plan, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            // Anthropic gates this endpoint on a CLI User-Agent. Without it
            // the request 401s even with a valid token.
            request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.121");

            using var response = await Http.Client.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (status == 401) return new ClaudeCredentials.ProbeOutcome.Unauthorized();
            if (status == 403) return new ClaudeCredentials.ProbeOutcome.ScopeInsufficient();
            if (status == 429) return new ClaudeCredentials.ProbeOutcome.RateLimited();
            if (status != 200) return new ClaudeCredentials.ProbeOutcome.OtherError($"HTTP {status}");

            // The endpoint also returns 200 with a rate_limit_error body
            // sometimes; don't trust the status code alone.
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var root = doc.RootElement;
            if (Jsonl.GetObject(root, "error") is { } error
                && Jsonl.GetString(error, "type") == "rate_limit_error")
            {
                return new ClaudeCredentials.ProbeOutcome.RateLimited();
            }
            return new ClaudeCredentials.ProbeOutcome.Success(new AppUsage(
                ParseClaudeWindow(Jsonl.GetObject(root, "five_hour"), periodSeconds: 5 * 3600),
                ParseClaudeWindow(Jsonl.GetObject(root, "seven_day"), periodSeconds: 7 * 86400),
                plan));
        }
        catch (Exception error)
        {
            return new ClaudeCredentials.ProbeOutcome.OtherError(error.Message);
        }
    }

    private static WindowUsage ParseClaudeWindow(JsonElement? obj, double periodSeconds)
    {
        if (obj is not { } window) return WindowUsage.Unknown;
        // Anthropic returns `utilization` as a percentage in [0, 100], not a
        // normalized fraction. Always divide by 100; clamp below.
        var raw = Jsonl.GetDouble(window, "utilization")
            ?? Jsonl.GetDouble(window, "used_percent")
            ?? 0;
        var normalized = raw / 100.0;
        DateTimeOffset? resetAt = null;
        if (Jsonl.GetDouble(window, "resets_at") is { } epoch)
        {
            resetAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000));
        }
        else if (Jsonl.GetString(window, "resets_at") is { } iso)
        {
            resetAt = Jsonl.ParseIso8601(iso);
        }
        return new WindowUsage(Math.Min(1, Math.Max(0, normalized)), resetAt, null, periodSeconds);
    }
}
