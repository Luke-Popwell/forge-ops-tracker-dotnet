using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

/// <summary>
/// End-to-end through a real ASP.NET Core pipeline (TestServer) with real endpoint routing, not
/// just the middleware class in isolation: exercises the actual
/// AddForgeOpsTracker/UseForgeOpsTrackerPerformanceTracking registration path a real host app
/// would use. Mirrors SessionTrackingMiddlewareTests, swapping IClient rather than
/// PerformanceFlusher itself, so the real PerformanceFlusher registered by AddForgeOpsTracker is
/// what's under test.
/// </summary>
public class PerformanceTrackingMiddlewareTests
{
    [Fact]
    public async Task RecordsTheMatchedRoutePatternNotTheRawPath()
    {
        var client = new RecordingClient();

        using var host = await BuildHost(client, endpoints =>
            endpoints.MapGet("/users/{id}", () => "ok")).StartAsync();

        var response = await host.GetTestClient().GetAsync("/users/42");
        Assert.True(response.IsSuccessStatusCode);

        var sample = await FlushAndCaptureSingle(host, client);
        // The route pattern, not "GET /users/42": a distinct user id must not explode into its
        // own separate transaction the way the literal path would.
        Assert.Equal("GET /users/{id}", sample.TransactionName);
        Assert.Equal(1, sample.RequestCount);
    }

    [Fact]
    public async Task FallsBackToTheRawPathWhenNoRouteMatches()
    {
        var client = new RecordingClient();

        using var host = await BuildHost(client, endpoints =>
            endpoints.MapGet("/users/{id}", () => "ok")).StartAsync();

        var response = await host.GetTestClient().GetAsync("/no-such-route");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        var sample = await FlushAndCaptureSingle(host, client);
        Assert.Equal("GET /no-such-route", sample.TransactionName);
    }

    [Fact]
    public async Task AggregatesMultipleRequestsToTheSameRouteIntoOneSample()
    {
        var client = new RecordingClient();

        using var host = await BuildHost(client, endpoints =>
            endpoints.MapGet("/users/{id}", () => "ok")).StartAsync();

        await host.GetTestClient().GetAsync("/users/1");
        await host.GetTestClient().GetAsync("/users/2");

        var sample = await FlushAndCaptureSingle(host, client);
        Assert.Equal("GET /users/{id}", sample.TransactionName);
        Assert.Equal(2, sample.RequestCount);
    }

    [Fact]
    public async Task RecordsAnAutomaticControllerBreadcrumbAlongsideTheThrownExceptionsReport()
    {
        List<TrackedEvent> delivered = new();
        var client = new RecordingClient();

        using var host = await new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development");
                });
                services.AddSingleton<IClient>(client);
                services.AddSingleton<IDeliveryQueue>(new RecordingDeliveryQueue(delivered));
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                // Tracker outer, PerformanceTracking inner (matches README.md's own documented
                // ordering): Tracker's catch block has to run after PerformanceTracking's own
                // finally has already recorded the breadcrumb, or the exception report would be
                // built one breadcrumb too early.
                app.UseForgeOpsTrackerBreadcrumbs();
                app.UseForgeOpsTracker();
                app.UseForgeOpsTrackerPerformanceTracking();
                app.UseEndpoints(endpoints => endpoints.MapGet("/users/{id}", _ => throw new InvalidOperationException("boom")));
            });
        }).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/users/42"));

        var reported = Assert.Single(delivered);
        var breadcrumb = Assert.Single(reported.Breadcrumbs!);
        Assert.Equal("controller", breadcrumb.Category);
        Assert.Equal("GET /users/{id}", breadcrumb.Message);
        Assert.Equal("/users/42", breadcrumb.Data["path"]);
    }

    [Fact]
    public async Task RecordsAnInfoLevelControllerBreadcrumbForASuccessfulTwoHundredResponse()
    {
        // A direct, class-level test of PerformanceTrackingMiddleware.InvokeAsync (no TestServer,
        // no HTTP round trip needed): the automatic breadcrumb's own message/category/level/data
        // shape is precise, class-internal logic best proven directly, the same reasoning
        // BreadcrumbBufferTests/ForgeOpsTrackerSdkTests already lean on the InternalsVisibleTo
        // seam for (see ForgeOpsTracker.Client.csproj's own comment). The genuinely end-to-end,
        // DI-registered pipeline path is what RecordsAnAutomaticControllerBreadcrumb... above
        // already proves instead.
        var configuration = new Configuration { Dsn = "https://key@tracker.example.com/api/v1/events", EnabledEnvironments = { "Development" } };
        ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = new BreadcrumbBuffer(configuration);
        var middleware = new PerformanceTrackingMiddleware(_ => Task.CompletedTask, new PerformanceFlusher(configuration, new RecordingClient()));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/users/42";
        context.Response.StatusCode = 200;

        await middleware.InvokeAsync(context);

        var breadcrumb = Assert.Single(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value!.All());
        Assert.Equal("controller", breadcrumb.Category);
        Assert.Equal("info", breadcrumb.Level);
        Assert.Equal("GET /users/42", breadcrumb.Message);
        Assert.Equal(200, breadcrumb.Data["status"]);
        Assert.Equal("/users/42", breadcrumb.Data["path"]);

        ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = null;
    }

    [Fact]
    public async Task RecordsAnErrorLevelControllerBreadcrumbForAFiveHundredResponse()
    {
        var configuration = new Configuration { Dsn = "https://key@tracker.example.com/api/v1/events", EnabledEnvironments = { "Development" } };
        ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = new BreadcrumbBuffer(configuration);
        var middleware = new PerformanceTrackingMiddleware(
            context => { context.Response.StatusCode = 503; return Task.CompletedTask; },
            new PerformanceFlusher(configuration, new RecordingClient()));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/users/42";

        await middleware.InvokeAsync(context);

        var breadcrumb = Assert.Single(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value!.All());
        Assert.Equal("error", breadcrumb.Level);
        Assert.Equal(503, breadcrumb.Data["status"]);

        ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = null;
    }

    [Fact]
    public async Task RecordsNothingWhenNoBreadcrumbBufferIsActive()
    {
        // BreadcrumbContextMiddleware never ran (not registered, or TrackBreadcrumbs off): no
        // buffer for RecordBreadcrumb to add to, so this is a no-op rather than a lazily-created
        // standalone trail; see ForgeOpsTrackerSdk.RecordBreadcrumb's own doc comment for why.
        Assert.Null(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value);
        var configuration = new Configuration { Dsn = "https://key@tracker.example.com/api/v1/events", EnabledEnvironments = { "Development" } };
        var middleware = new PerformanceTrackingMiddleware(_ => Task.CompletedTask, new PerformanceFlusher(configuration, new RecordingClient()));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/users/42";

        await middleware.InvokeAsync(context);

        Assert.Null(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value);
    }

    [Fact]
    public async Task RecordsNoAutomaticBreadcrumbWhenTrackBreadcrumbsIsOffEvenWithBreadcrumbContextRegistered()
    {
        List<TrackedEvent> delivered = new();
        var client = new RecordingClient();

        using var host = await new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development");
                    c.TrackBreadcrumbs = false;
                });
                services.AddSingleton<IClient>(client);
                services.AddSingleton<IDeliveryQueue>(new RecordingDeliveryQueue(delivered));
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                app.UseForgeOpsTrackerBreadcrumbs();
                app.UseForgeOpsTracker();
                app.UseForgeOpsTrackerPerformanceTracking();
                app.UseEndpoints(endpoints => endpoints.MapGet("/users/{id}", _ => throw new InvalidOperationException("boom")));
            });
        }).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/users/42"));

        var reported = Assert.Single(delivered);
        Assert.Null(reported.Breadcrumbs);
    }

    private static IHostBuilder BuildHost(RecordingClient client, Action<IEndpointRouteBuilder> configureEndpoints) =>
        new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development"); // TestServer's default hosting environment
                });

                // Swap in a client that just records performance samples synchronously, instead
                // of a real HTTP round trip; the real PerformanceFlusher registered by
                // AddForgeOpsTracker is still what's under test here.
                services.AddSingleton<IClient>(client);
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                app.UseForgeOpsTrackerPerformanceTracking();
                app.UseEndpoints(configureEndpoints);
            });
        });

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

    private static async Task<PerformanceSample> FlushAndCaptureSingle(IHost host, RecordingClient client)
    {
        var flusher = host.Services.GetRequiredService<PerformanceFlusher>();
        await flusher.FlushAsync();
        var batch = Assert.Single(client.Delivered);
        return Assert.Single(batch.Samples);
    }

    private sealed class RecordingClient : IClient
    {
        public List<PerformanceSamplesBatch> Delivered { get; } = new();

        public Task<bool> DeliverAsync(TrackedEvent payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> DeliverPerformanceSamplesAsync(PerformanceSamplesBatch payload, CancellationToken cancellationToken = default)
        {
            Delivered.Add(payload);
            return Task.FromResult(true);
        }
    }
}
