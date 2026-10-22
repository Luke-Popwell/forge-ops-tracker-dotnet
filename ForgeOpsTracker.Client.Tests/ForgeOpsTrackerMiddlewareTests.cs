using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

/// <summary>
/// End-to-end through a real ASP.NET Core pipeline (TestServer), not just
/// the middleware class in isolation: exercises the actual
/// AddForgeOpsTracker/UseForgeOpsTracker registration path a real host app
/// would use.
/// </summary>
public class ForgeOpsTrackerMiddlewareTests
{
    [Fact]
    public async Task ReportsTheExceptionAndStillLetsItPropagate()
    {
        List<TrackedEvent> delivered = new();

        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddForgeOpsTracker(c =>
                    {
                        c.Dsn = "https://key@tracker.example.com/api/v1/events";
                        c.EnabledEnvironments.Add("Development"); // TestServer's default hosting environment
                        c.DetectChanges = false; // this host delivers through the real Client, so no startup snapshot
                    });

                    // Swap in a delivery queue that just records payloads
                    // synchronously, instead of a real HTTP round trip.
                    services.AddSingleton<IDeliveryQueue>(new RecordingDeliveryQueue(delivered));
                });
                webHost.Configure(app =>
                {
                    app.UseForgeOpsTracker();
                    app.Run(_ => throw new InvalidOperationException("boom"));
                });
            })
            .StartAsync();

        var client = host.GetTestClient();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/"));

        Assert.Equal("boom", thrown.Message);
        var reported = Assert.Single(delivered);
        Assert.Equal("System.InvalidOperationException", reported.ExceptionClass);
        Assert.Equal("boom", reported.Message);
        Assert.Equal("/", reported.Context!["path"]);
        Assert.Equal("GET", reported.Context["method"]);
    }

    private sealed class RecordingDeliveryQueue : IDeliveryQueue
    {
        private readonly List<TrackedEvent> _delivered;
        public RecordingDeliveryQueue(List<TrackedEvent> delivered) => _delivered = delivered;
        public bool Push(TrackedEvent payload)
        {
            _delivered.Add(payload);
            return true;
        }
    }
}
