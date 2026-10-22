using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

/// <summary>
/// End-to-end through a real ASP.NET Core pipeline (TestServer), not just the middleware class in
/// isolation: exercises the actual AddForgeOpsTracker/UseForgeOpsTrackerSessionTracking
/// registration path a real host app would use. Mirrors ForgeOpsTrackerMiddlewareTests, swapping
/// IClient (the same seam Client already exposes for testing) rather than SessionFlusher itself,
/// so the real SessionFlusher registered by AddForgeOpsTracker is what's under test.
/// </summary>
public class SessionTrackingMiddlewareTests
{
    [Fact]
    public async Task RecordsACrashFreeSessionForASuccessfulRequest()
    {
        var client = new RecordingClient();

        using var host = await BuildHost(client, app => app.Run(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        })).StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        Assert.True(response.IsSuccessStatusCode);

        var checkin = await FlushAndCapture(host, client);
        Assert.Equal(1, checkin.SessionsCount);
        Assert.Equal(0, checkin.CrashedSessionsCount);
    }

    [Fact]
    public async Task RecordsACrashedSessionAndStillLetsTheExceptionPropagate()
    {
        var client = new RecordingClient();

        using var host = await BuildHost(client, app =>
            app.Run(_ => throw new InvalidOperationException("boom"))).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/"));

        var checkin = await FlushAndCapture(host, client);
        Assert.Equal(1, checkin.SessionsCount);
        Assert.Equal(1, checkin.CrashedSessionsCount);
    }

    [Fact]
    public async Task RecordsACrashedSessionForAFiveHundredResponseWithNoThrownException()
    {
        var client = new RecordingClient();

        using var host = await BuildHost(client, app => app.Run(context =>
        {
            context.Response.StatusCode = 503;
            return Task.CompletedTask;
        })).StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var checkin = await FlushAndCapture(host, client);
        Assert.Equal(1, checkin.SessionsCount);
        Assert.Equal(1, checkin.CrashedSessionsCount);
    }

    private static IHostBuilder BuildHost(RecordingClient client, Action<IApplicationBuilder> configure) =>
        new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development"); // TestServer's default hosting environment
                });

                // Swap in a client that just records session checkins synchronously, instead of a
                // real HTTP round trip; the real SessionFlusher registered by
                // AddForgeOpsTracker is still what's under test here.
                services.AddSingleton<IClient>(client);
            });
            webHost.Configure(app =>
            {
                app.UseForgeOpsTrackerSessionTracking();
                configure(app);
            });
        });

    private static async Task<SessionCheckin> FlushAndCapture(IHost host, RecordingClient client)
    {
        var flusher = host.Services.GetRequiredService<SessionFlusher>();
        await flusher.FlushAsync();
        return Assert.Single(client.Delivered);
    }

    private sealed class RecordingClient : IClient
    {
        public List<SessionCheckin> Delivered { get; } = new();

        public Task<bool> DeliverAsync(TrackedEvent payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> DeliverSessionCheckinAsync(SessionCheckin payload, CancellationToken cancellationToken = default)
        {
            Delivered.Add(payload);
            return Task.FromResult(true);
        }
    }
}
