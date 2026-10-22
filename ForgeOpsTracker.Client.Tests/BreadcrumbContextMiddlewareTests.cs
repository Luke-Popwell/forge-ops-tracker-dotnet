using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

/// <summary>
/// End-to-end through a real ASP.NET Core pipeline (TestServer), the same technique
/// ForgeOpsTrackerMiddlewareTests/UserContextMiddlewareTests already use: exercises the actual
/// AddForgeOpsTracker/UseForgeOpsTrackerBreadcrumbs registration path a real host app would use,
/// observed through what actually gets delivered rather than by reaching into the AsyncLocal
/// directly (ForgeOpsTrackerSdkTests covers the facade's own isolation guarantee that way instead).
/// </summary>
public class BreadcrumbContextMiddlewareTests
{
    [Fact]
    public async Task AttachesAManuallyAddedBreadcrumbToAReportedException()
    {
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, app => app.Run(_ =>
        {
            ForgeOpsTrackerSdk.AddBreadcrumb("charged card", category: "billing", data: new Dictionary<string, object?> { ["order_id"] = 7 });
            throw new InvalidOperationException("boom");
        })).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/"));

        var reported = Assert.Single(delivered);
        var breadcrumb = Assert.Single(reported.Breadcrumbs!);
        Assert.Equal("billing", breadcrumb.Category);
        Assert.Equal("charged card", breadcrumb.Message);
        Assert.Equal(7, breadcrumb.Data["order_id"]);
    }

    [Fact]
    public async Task OmitsBreadcrumbsEntirelyWhenNoneWereAdded()
    {
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, app =>
            app.Run(_ => throw new InvalidOperationException("boom"))).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/"));

        var reported = Assert.Single(delivered);
        Assert.Null(reported.Breadcrumbs);
    }

    [Fact]
    public async Task GivesEachRequestItsOwnFreshTrailNotOneAccumulatedAcrossRequests()
    {
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, app => app.Run(context =>
        {
            var id = context.Request.Query["id"].ToString();
            ForgeOpsTrackerSdk.AddBreadcrumb($"breadcrumb-{id}");
            throw new InvalidOperationException($"boom-{id}");
        })).StartAsync();

        var client = host.GetTestClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/?id=1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/?id=2"));

        Assert.Equal(2, delivered.Count);
        Assert.Equal(new[] { "breadcrumb-1" }, delivered[0].Breadcrumbs!.Select(b => b.Message));
        Assert.Equal(new[] { "breadcrumb-2" }, delivered[1].Breadcrumbs!.Select(b => b.Message));
    }

    [Fact]
    public async Task IsolatesTrailsBetweenGenuinelyConcurrentInterleavedRequests()
    {
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, app => app.Run(async context =>
        {
            var id = context.Request.Query["id"].ToString();
            // Staggered delays so the two requests' own continuations actually interleave rather
            // than happening to run back-to-back; the same technique sdks/node's own Fastify
            // concurrency test uses to confirm AsyncLocalStorage isolation for real, not by
            // assumption.
            await Task.Delay(id == "1" ? 30 : 5);
            ForgeOpsTrackerSdk.AddBreadcrumb($"breadcrumb-{id}");
            throw new InvalidOperationException($"boom-{id}");
        })).StartAsync();

        var client = host.GetTestClient();
        var request1 = Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/?id=1"));
        var request2 = Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/?id=2"));
        await Task.WhenAll(request1, request2);

        Assert.Equal(2, delivered.Count);
        foreach (var reported in delivered)
        {
            var expectedId = reported.Message.Split('-')[1];
            var breadcrumb = Assert.Single(reported.Breadcrumbs!);
            Assert.Equal($"breadcrumb-{expectedId}", breadcrumb.Message);
        }
    }

    [Fact]
    public async Task ClearsTheTrailEvenWhenTheAppRaises()
    {
        // Proven the same way UserContextMiddlewareTests proves CurrentUser gets cleared: a
        // second, otherwise-identical request must not see anything left over from the first.
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, app => app.Run(_ =>
        {
            ForgeOpsTrackerSdk.AddBreadcrumb("only-in-first-request");
            throw new InvalidOperationException("boom");
        })).StartAsync();

        var client = host.GetTestClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/"));

        Assert.Equal(2, delivered.Count);
        Assert.Equal(new[] { "only-in-first-request" }, delivered[0].Breadcrumbs!.Select(b => b.Message));
        Assert.Equal(new[] { "only-in-first-request" }, delivered[1].Breadcrumbs!.Select(b => b.Message));
    }

    [Fact]
    public async Task ManualBreadcrumbsStillWorkWhenTrackBreadcrumbsIsOff()
    {
        // AddBreadcrumb's own lazy-creation path (see ForgeOpsTrackerSdk's doc comment) works
        // regardless of TrackBreadcrumbs: that flag only gates BreadcrumbContextMiddleware's own
        // fresh-buffer setup and the automatic controller breadcrumb, matching
        // gems/forge_ops_tracker's own "manual API works regardless of the automatic sources"
        // contract.
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, app => app.Run(_ =>
        {
            ForgeOpsTrackerSdk.AddBreadcrumb("added despite the flag being off");
            throw new InvalidOperationException("boom");
        }), configureTrackBreadcrumbs: c => c.TrackBreadcrumbs = false).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/"));

        var reported = Assert.Single(delivered);
        Assert.Equal(new[] { "added despite the flag being off" }, reported.Breadcrumbs!.Select(b => b.Message));
    }

    private static IHostBuilder BuildHost(List<TrackedEvent> delivered, Action<IApplicationBuilder> configure, Action<Configuration>? configureTrackBreadcrumbs = null) =>
        new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development"); // TestServer's default hosting environment
                    c.DetectChanges = false; // this host delivers through the real Client, so no startup snapshot
                    configureTrackBreadcrumbs?.Invoke(c);
                });

                services.AddSingleton<IDeliveryQueue>(new RecordingDeliveryQueue(delivered));
            });
            webHost.Configure(app =>
            {
                app.UseForgeOpsTrackerBreadcrumbs();
                app.UseForgeOpsTracker();
                configure(app);
            });
        });

    private sealed class RecordingDeliveryQueue : IDeliveryQueue
    {
        private readonly object _lock = new();
        private readonly List<TrackedEvent> _delivered;
        public RecordingDeliveryQueue(List<TrackedEvent> delivered) => _delivered = delivered;

        public bool Push(TrackedEvent payload)
        {
            // Two genuinely concurrent requests can both report at once (see the interleaving
            // test above): a plain List<T> isn't safe for that without a lock.
            lock (_lock)
            {
                _delivered.Add(payload);
            }
            return true;
        }
    }
}
