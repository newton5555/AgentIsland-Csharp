using System.Net.Http;

namespace AgentIsland.Usage;

/// One shared HttpClient for provider calls; per-request headers are set on
/// the request messages, never on defaults, so the two providers can't
/// contaminate each other.
public static class Http
{
    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
}
