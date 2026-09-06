using System.Net;
using System.Net.Http;
using AgentIsland.Backend.Network;
using AgentIsland.Core;
using AgentIsland.Core.Network;
using AgentIsland.Core.Usage;
using Xunit;

namespace AgentIsland.Tests;

public class ResilienceHttpTests
{
    [Fact]
    public void TestAll() => RunAll();

    internal static void RunAll()
    {
        TestOfflineFastFailThrowsImmediately();
        TestOnlinePassesThroughToInnerHandler();
        TestOfflineFastFailPreservesUsageCache();
        TestTransientHttpErrorRetryBehavior();
    }

    private static void TestOfflineFastFailThrowsImmediately()
    {
        var connectivity = new MockNetworkConnectivity(isAvailable: false);
        var innerHandler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new OfflineFastFailHandler(connectivity, innerHandler));

        var ex = Assert.Throws<HttpRequestException>(() =>
        {
            client.Send(new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/test"));
        });

        Assert.Contains("network drop", ex.Message);
        Assert.Equal(0, innerHandler.RequestCount); // Crucial: Inner handler never touched
    }

    private static void TestOnlinePassesThroughToInnerHandler()
    {
        var connectivity = new MockNetworkConnectivity(isAvailable: true);
        var expectedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}")
        };
        var innerHandler = new RecordingHandler(expectedResponse);
        using var client = new HttpClient(new OfflineFastFailHandler(connectivity, innerHandler));

        using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
    }

    private static void TestOfflineFastFailPreservesUsageCache()
    {
        // 1. Initial successful fetch cached
        var now = DateTimeOffset.Now;
        var validClaude = new AppUsage(
            new WindowUsage(45, now.AddHours(3), null, 5 * 3600),
            new WindowUsage(20, now.AddDays(4), null, 7 * 86400),
            "Pro");

        var snapshot1 = UsageCachePolicy.SnapshotForSave(
            validClaude, AppUsage.Empty, null, now, fetchedClaude: true, fetchedCodex: false);
        Assert.NotNull(snapshot1);
        Assert.Equal(45, snapshot1!.Claude.FiveHour.UsedPercent);

        // 2. Subsequent fetch fails due to offline "network drop"
        var errorClaude = AppUsage.ErrorPair("network drop");
        var snapshot2 = UsageCachePolicy.SnapshotForSave(
            errorClaude, AppUsage.Empty, snapshot1, now.AddMinutes(5), fetchedClaude: true, fetchedCodex: false);

        // SnapshotForSave correctly returns null to avoid overwriting existing disk cache
        Assert.Null(snapshot2);

        // 3. And RestoredSnapshot preserves and restores the existing snapshot within MaxAge
        var restored = UsageCachePolicy.RestoredSnapshot(snapshot1, now.AddMinutes(5), TimeSpan.FromHours(2));
        Assert.NotNull(restored);
        Assert.Equal(45, restored!.Claude.FiveHour.UsedPercent);
        Assert.Equal(20, restored.Claude.Weekly.UsedPercent);
        Assert.Equal("Pro", restored.Claude.Plan);
    }

    private static void TestTransientHttpErrorRetryBehavior()
    {
        var connectivity = new MockNetworkConnectivity(isAvailable: true);
        var retryingHandler = new FailThenSucceedHandler(failAttempts: 1);

        // Manual retry simulation matching Polly pipeline intent
        using var client = new HttpClient(new OfflineFastFailHandler(connectivity, retryingHandler));

        HttpResponseMessage? response = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                response = client.Send(new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance"));
                if (response.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException)
            {
                if (i == 2) throw;
            }
        }

        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
        Assert.Equal(2, retryingHandler.TotalAttempts);
    }

    private sealed class MockNetworkConnectivity : INetworkConnectivityService
    {
        public bool IsNetworkAvailable { get; set; }
        public event EventHandler<bool>? NetworkAvailabilityChanged;

        public MockNetworkConnectivity(bool isAvailable)
        {
            IsNetworkAvailable = isAvailable;
        }

        public void SetAvailable(bool available)
        {
            IsNetworkAvailable = available;
            NetworkAvailabilityChanged?.Invoke(this, available);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public int RequestCount { get; private set; }

        public RecordingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return _response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_response);
        }
    }

    private sealed class FailThenSucceedHandler : HttpMessageHandler
    {
        private readonly int _failAttempts;
        public int TotalAttempts { get; private set; }

        public FailThenSucceedHandler(int failAttempts)
        {
            _failAttempts = failAttempts;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TotalAttempts++;
            if (TotalAttempts <= _failAttempts)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }
}
