using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentIsland.Core.Agents;
using AgentMonitoring.Consumption;
using AgentMonitoring.Host;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentMonitoring.Tests;

public sealed class LocalMonitoringApiTests
{
    [Fact]
    public async Task Api_IsLoopbackOnly_AndReturnsVersionedJson()
    {
        var port = FindFreePort();
        var storePath = Path.Combine(Path.GetTempPath(), $"monitor-api-{Guid.NewGuid():N}.json");
        var home = Path.Combine(Path.GetTempPath(), $"monitor-api-home-{Guid.NewGuid():N}");
        WriteSession(home);
        var options = new HeadlessOptions
        {
            ApiPort = port,
            CodexHomes = new[] { home },
            ConsumptionStorePath = storePath,
            CollectInterval = TimeSpan.FromMinutes(5),
        };

        var previousUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Microsoft.AspNetCore.Builder.WebApplication app;
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:60001");
            app = HeadlessServices.CreateWebApplication(options);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previousUrls);
        }
        await using var appLifetime = app;
        try
        {
            await appLifetime.StartAsync();
            var bound = appLifetime.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            if (bound is { Count: > 0 })
            {
                Assert.All(bound, address =>
                {
                    var uri = new Uri(address);
                    Assert.True(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
                });
            }

            var listenerAddresses = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(address => address.Port == port)
                .ToList();
            Assert.NotEmpty(listenerAddresses);
            Assert.All(listenerAddresses, address => Assert.True(IPAddress.IsLoopback(address.Address)));

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var response = await client.GetAsync("/api/v1/agents");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var codex = json.RootElement.EnumerateArray()
                .Single(agent => agent.GetProperty("agentKey").GetString() == "codex");
            Assert.Equal("unknown", codex.GetProperty("activity").GetProperty("state").GetString());
            Assert.False(codex.GetProperty("activity").GetProperty("available").GetBoolean());

            using var health = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            using var invalidRange = await client.GetAsync(
                "/api/v1/consumption?from=2000-01-01T00%3A00%3A00Z&to=2035-01-01T00%3A00%3A00Z");
            Assert.Equal(HttpStatusCode.BadRequest, invalidRange.StatusCode);

            using var externalHostRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            externalHostRequest.Headers.Host = "attacker.example";
            using var rejectedHost = await client.SendAsync(externalHostRequest);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedHost.StatusCode);
        }
        finally
        {
            await appLifetime.StopAsync();
            TryDelete(storePath);
            TryDeleteDir(home);
        }
    }

    [Fact]
    public async Task CollectorFailure_IsReportedAndRetriedWithoutStoppingHost()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"monitor-retry-{Guid.NewGuid():N}.json");
        var collector = new FailOnceCollector();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddHeadlessMonitoring(new HeadlessOptions
        {
            ConsumptionStorePath = storePath,
            CollectInterval = TimeSpan.FromMilliseconds(40),
        });
        builder.Services.RemoveAll<IConsumptionCollector>();
        builder.Services.AddSingleton<IConsumptionCollector>(collector);
        builder.Services.AddHostedService<HeadlessCollectWorker>();
        using var host = builder.Build();
        try
        {
            await host.StartAsync();
            await collector.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var health = host.Services.GetRequiredService<HeadlessCollectorHealth>();
            await WaitUntil(() => health.Read().ConsecutiveFailures == 1);
            Assert.True(health.Read().Running);
            Assert.NotNull(health.Read().LastError);

            collector.AllowRetry.TrySetResult();
            await WaitUntil(() => health.Read().LastSuccessAt is not null);
            Assert.Equal(0, health.Read().ConsecutiveFailures);
            Assert.Null(health.Read().LastError);
        }
        finally
        {
            await host.StopAsync();
            TryDelete(storePath);
        }
    }

    [Fact]
    public void HeadlessOptions_RejectsNonLoopbackBrowserOrigins()
    {
        var options = new HeadlessOptions { AllowedOrigins = new[] { "https://example.com" } };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met before timeout.");
    }

    private static void WriteSession(string home)
    {
        var dir = Path.Combine(home, "sessions");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "local-api.jsonl"), new[]
        {
            "{\"type\":\"session_meta\",\"timestamp\":\"2026-07-16T10:00:00Z\",\"payload\":{\"id\":\"local-api\"}}",
            "{\"type\":\"turn_context\",\"timestamp\":\"2026-07-16T10:00:00Z\",\"payload\":{\"model\":\"gpt-5.4\"}}",
            "{\"type\":\"event_msg\",\"timestamp\":\"2026-07-16T10:00:01Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":40,\"cached_input_tokens\":0,\"output_tokens\":8},\"total_token_usage\":{\"input_tokens\":40,\"output_tokens\":8}}}}",
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private sealed class FailOnceCollector : IConsumptionCollector
    {
        private int _calls;
        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRetry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task CollectAsync(AgentKey agent, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstAttempt.TrySetResult();
                throw new IOException("transient test failure");
            }

            await AllowRetry.Task.WaitAsync(cancellationToken);
        }
    }
}
