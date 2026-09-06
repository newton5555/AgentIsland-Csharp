using System.Net;
using System.Net.Http;
using AgentIsland.Core.Network;

namespace AgentIsland.Backend.Network;

/// <summary>
/// HTTP delegating handler that immediately short-circuits with a fast failure when
/// offline, preventing long blocking socket connection/DNS timeouts.
/// </summary>
public sealed class OfflineFastFailHandler : DelegatingHandler
{
    private readonly INetworkConnectivityService _connectivity;

    public OfflineFastFailHandler(INetworkConnectivityService connectivity)
    {
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
    }

    public OfflineFastFailHandler(INetworkConnectivityService connectivity, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_connectivity.IsNetworkAvailable)
        {
            throw new HttpRequestException("network drop", null, HttpStatusCode.ServiceUnavailable);
        }
        return base.Send(request, cancellationToken);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_connectivity.IsNetworkAvailable)
        {
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("network drop", null, HttpStatusCode.ServiceUnavailable));
        }
        return base.SendAsync(request, cancellationToken);
    }
}
