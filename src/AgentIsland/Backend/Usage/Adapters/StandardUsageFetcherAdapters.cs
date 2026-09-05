using AgentIsland.Core;
using AgentIsland.Core.Agents;

namespace AgentIsland.Backend.Usage.Adapters;

public sealed class ClaudeUsageFetcherAdapter : IUsageFetcher
{
    public async ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default)
    {
        return await UsageFetcher.FetchClaude(ct).ConfigureAwait(false);
    }
}

public sealed class CodexUsageFetcherAdapter : IUsageFetcher
{
    public async ValueTask<AppUsage> FetchUsageAsync(CancellationToken ct = default)
    {
        return await UsageFetcher.FetchCodex(ct).ConfigureAwait(false);
    }
}
