using System.Net.Http;
using AgentIsland.Backend.Network;

namespace AgentIsland.Backend.Usage;

/// <summary>
/// HttpClient provider configured via IHttpClientFactory when running inside Generic Host,
/// with graceful fallback for standalone and test environments.
/// </summary>
public static class Http
{
    public const string ResilientClientName = "AgentIsland.Resilient";

    private static IHttpClientFactory? _factory;

    public static void Configure(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public static HttpClient Client => _factory?.CreateClient(ResilientClientName) ?? FallbackClient;

    private static readonly HttpClient FallbackClient = new(
        new OfflineFastFailHandler(
            new SystemNetworkConnectivityService(),
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            }))
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
}
