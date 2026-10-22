using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

public class TracingTests
{
    private static Configuration NewConfiguration(TimeSpan? threshold = null) => new()
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        Environment = "production",
        Release = "abc123",
        EnabledEnvironments = { "production" },
        TraceCaptureThreshold = threshold ?? TimeSpan.FromMilliseconds(10)
    };

    [Fact]
    public void SpansUriSwapsTheTrailingEventsSegment()
    {
        Assert.Equal("https://tracker.example.com/api/v1/spans", NewConfiguration().SpansUri()!.ToString());
    }

    [Fact]
    public void NestsSpansUnderTheOpenOneAndTheRootWithTheWireShape()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        ForgeOpsTrackerSdk.CurrentTrace.Value = buffer;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = buffer.RootSpanId;
        try
        {
            using (ForgeOpsTrackerSdk.StartSpan("outer", "service", new Dictionary<string, object?> { ["k"] = "v" }))
            {
                ForgeOpsTrackerSdk.RecordSpan("SELECT", "database", DateTimeOffset.UtcNow, 3);
            }
            ForgeOpsTrackerSdk.RecordSpan("sibling", "database", DateTimeOffset.UtcNow, 1);
        }
        finally
        {
            ForgeOpsTrackerSdk.CurrentTrace.Value = null;
            ForgeOpsTrackerSdk.CurrentSpanId.Value = null;
        }

        var payload = buffer.Finish("GET /x", DateTimeOffset.UtcNow, 1500)!;
        var byName = payload.Spans.ToDictionary(s => s.Name);
        Assert.Equal(32, payload.TraceId.Length);
        Assert.Null(byName["GET /x"].ParentSpanId);
        Assert.Equal("controller", byName["GET /x"].Kind);
        Assert.Equal(byName["outer"].SpanId, byName["SELECT"].ParentSpanId);
        Assert.Equal(buffer.RootSpanId, byName["outer"].ParentSpanId);
        Assert.Equal(buffer.RootSpanId, byName["sibling"].ParentSpanId);
        Assert.Equal(16, byName["outer"].SpanId.Length);
        Assert.Equal("production", byName["outer"].Environment);
        Assert.Equal("abc123", byName["outer"].Release);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", byName["outer"].StartedAt);
        Assert.Contains("\"parent_span_id\":null", JsonSerializer.Serialize(payload));
    }

    [Fact]
    public void DropsTheTraceWhenTheRootIsUnderTheThreshold()
    {
        var buffer = new SpanBuffer(NewConfiguration(TimeSpan.FromSeconds(1)));
        Assert.Null(buffer.Finish("GET /fast", DateTimeOffset.UtcNow, 999));
    }

    [Fact]
    public void SendsAnUnknownKindAsOtherSinceTheServerWouldRejectTheWholeTrace()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        buffer.Record("a", buffer.RootSpanId, "q", "db", DateTimeOffset.UtcNow, 1, null);
        buffer.Record("b", buffer.RootSpanId, "r", "database", DateTimeOffset.UtcNow, 1, null);
        var spans = buffer.Finish("root", DateTimeOffset.UtcNow, 2000)!.Spans;
        Assert.Equal("other", spans[1].Kind);
        Assert.Equal("database", spans[2].Kind);
    }

    [Fact]
    public void CapsATraceAtFiveHundredSpansIncludingTheRoot()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        for (var i = 0; i < 700; i++) buffer.Record("id", buffer.RootSpanId, "q", "database", DateTimeOffset.UtcNow, 1, null);
        Assert.Equal(500, buffer.Finish("GET /x", DateTimeOffset.UtcNow, 2000)!.Spans.Count);
    }

    [Fact]
    public void SpanOutsideATraceJustRunsTheWork()
    {
        Assert.Equal(7, ForgeOpsTrackerSdk.Span("free", () => 7));
    }

    [Fact]
    public async Task ParallelBranchesEachParentUnderTheirOwnOpenSpan()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        ForgeOpsTrackerSdk.CurrentTrace.Value = buffer;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = buffer.RootSpanId;
        try
        {
            async Task<int> Branch(string name, int delayMs) =>
                await ForgeOpsTrackerSdk.SpanAsync(name, async () =>
                {
                    await Task.Delay(delayMs);
                    ForgeOpsTrackerSdk.RecordSpan(name + " child", "database", DateTimeOffset.UtcNow, 1);
                    return 1;
                });

            await Task.WhenAll(Branch("a", 40), Branch("b", 10));
            // Back in the parent flow, the current span is the root again.
            Assert.Equal(buffer.RootSpanId, ForgeOpsTrackerSdk.CurrentSpanId.Value);
        }
        finally
        {
            ForgeOpsTrackerSdk.CurrentTrace.Value = null;
            ForgeOpsTrackerSdk.CurrentSpanId.Value = null;
        }

        var byName = buffer.Finish("root", DateTimeOffset.UtcNow, 1000)!.Spans.ToDictionary(s => s.Name);
        Assert.Equal(byName["a"].SpanId, byName["a child"].ParentSpanId);
        Assert.Equal(byName["b"].SpanId, byName["b child"].ParentSpanId);
        Assert.Equal(buffer.RootSpanId, byName["a"].ParentSpanId);
        Assert.Equal(buffer.RootSpanId, byName["b"].ParentSpanId);
    }

    [Fact]
    public void SpanRecordsAndRethrowsUnchangedWhenTheWorkThrows()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        ForgeOpsTrackerSdk.CurrentTrace.Value = buffer;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = buffer.RootSpanId;
        var failure = new InvalidOperationException("boom");
        try
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => ForgeOpsTrackerSdk.Span<int>("bad", () => throw failure));
            Assert.Same(failure, thrown);
        }
        finally
        {
            ForgeOpsTrackerSdk.CurrentTrace.Value = null;
            ForgeOpsTrackerSdk.CurrentSpanId.Value = null;
        }
        Assert.Contains(buffer.Finish("root", DateTimeOffset.UtcNow, 1000)!.Spans, s => s.Name == "bad");
    }

    [Fact]
    public async Task ClientPostsTheTraceToTheSpansEndpoint()
    {
        string? url = null;
        string? body = null;
        var handler = new FakeHandler(async request =>
        {
            url = request.RequestUri!.ToString();
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var client = new Client(NewConfiguration(), new HttpClient(handler));

        var ok = await client.DeliverSpansAsync(new TracePayload { TraceId = "t", Spans = new[] { new Span { SpanId = "s", Name = "n" } } });

        Assert.True(ok);
        Assert.Equal("https://tracker.example.com/api/v1/spans", url);
        Assert.Contains("\"trace_id\":\"t\"", body);
    }

    [Fact]
    public async Task HttpHandlerRecordsAnHttpSpanNamedMethodAndHostNeverThePath()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        ForgeOpsTrackerSdk.CurrentTrace.Value = buffer;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = buffer.RootSpanId;
        try
        {
            var handler = new TracingHttpMessageHandler(NewConfiguration()) { InnerHandler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))) };
            using var http = new HttpClient(handler);
            var response = await http.GetAsync("https://api.example.com/users/42?token=secret");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            // This SDK's own delivery is never traced.
            await http.PostAsync("https://tracker.example.com/api/v1/spans", new StringContent("{}"));

            var failing = new TracingHttpMessageHandler(NewConfiguration()) { InnerHandler = new FakeHandler(_ => throw new HttpRequestException("down")) };
            using var failingHttp = new HttpClient(failing);
            await Assert.ThrowsAsync<HttpRequestException>(() => failingHttp.GetAsync("https://flaky.example.com/x"));
        }
        finally
        {
            ForgeOpsTrackerSdk.CurrentTrace.Value = null;
            ForgeOpsTrackerSdk.CurrentSpanId.Value = null;
        }

        var spans = buffer.Finish("root", DateTimeOffset.UtcNow, 1000)!.Spans.Where(s => s.Kind == "http").ToList();
        Assert.Equal(2, spans.Count);
        var ok = spans.Single(s => s.Name == "GET api.example.com");
        Assert.Equal(404, ok.Data["status"]);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(spans));
        Assert.Equal("HttpRequestException", spans.Single(s => s.Name == "GET flaky.example.com").Data["error"]);
    }

    [Fact]
    public async Task HttpHandlerRecordsNothingOutsideATrace()
    {
        var handler = new TracingHttpMessageHandler(NewConfiguration()) { InnerHandler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))) };
        using var http = new HttpClient(handler);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("https://api.example.com/")).StatusCode);
    }

    [Fact]
    public async Task ASlowRequestReportsItsTraceThroughTheRealPipeline()
    {
        var client = new RecordingClient();
        using var host = await BuildHost(client, endpoints =>
            endpoints.MapGet("/users/{id}", async (IHttpClientFactory factory) =>
            {
                await ForgeOpsTrackerSdk.SpanAsync("load user", async () =>
                {
                    await Task.Delay(50);
                    return 1;
                }, "database");
                using var http = factory.CreateClient("outbound");
                await http.GetAsync("https://api.example.com/things");
                return "ok";
            })).StartAsync();

        var response = await host.GetTestClient().GetAsync("/users/42");
        Assert.True(response.IsSuccessStatusCode);

        var trace = await client.NextTrace();
        var byName = trace.Spans.ToDictionary(s => s.Name);
        Assert.Null(byName["GET /users/{id}"].ParentSpanId);
        Assert.Equal(byName["GET /users/{id}"].SpanId, byName["load user"].ParentSpanId);
        Assert.Equal(byName["GET /users/{id}"].SpanId, byName["GET api.example.com"].ParentSpanId);
        Assert.Equal("http", byName["GET api.example.com"].Kind);
    }

    [Fact]
    public async Task AFastRequestSendsNothingAndTrackTracingOffDisablesTheFeature()
    {
        var fast = new RecordingClient();
        using (var host = await BuildHost(fast, endpoints => endpoints.MapGet("/fast", () => "ok"), threshold: TimeSpan.FromSeconds(30)).StartAsync())
        {
            await host.GetTestClient().GetAsync("/fast");
            await Task.Delay(200);
        }
        Assert.Empty(fast.Traces);

        var off = new RecordingClient();
        using (var host = await BuildHost(off, endpoints => endpoints.MapGet("/slow", async () => { await Task.Delay(50); return "ok"; }), tracing: false).StartAsync())
        {
            await host.GetTestClient().GetAsync("/slow");
            await Task.Delay(200);
        }
        Assert.Empty(off.Traces);
    }

    [Fact]
    public async Task TraceAsyncSendsAStandaloneTraceForNonWebCode()
    {
        // Uses the ambient configuration; with no DSN configured it must simply run the work.
        var result = await ForgeOpsTrackerSdk.TraceAsync("job", () => Task.FromResult(5));
        Assert.Equal(5, result);
    }

    private static IHostBuilder BuildHost(RecordingClient client, Action<IEndpointRouteBuilder> configureEndpoints, TimeSpan? threshold = null, bool tracing = true) =>
        new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development");
                    c.TrackTracing = tracing;
                    c.TraceCaptureThreshold = threshold ?? TimeSpan.FromMilliseconds(20);
                });
                services.AddSingleton<IClient>(client);
                services.AddHttpClient("outbound").ConfigurePrimaryHttpMessageHandler(() =>
                    new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                app.UseForgeOpsTrackerPerformanceTracking();
                app.UseEndpoints(configureEndpoints);
            });
        });

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _respond(request);
    }

    private sealed class RecordingClient : IClient
    {
        private readonly TaskCompletionSource<TracePayload> _first = new();

        public List<TracePayload> Traces { get; } = new();

        public Task<bool> DeliverAsync(TrackedEvent payload, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeliverSpansAsync(TracePayload payload, CancellationToken cancellationToken = default)
        {
            lock (Traces) Traces.Add(payload);
            _first.TrySetResult(payload);
            return Task.FromResult(true);
        }

        public async Task<TracePayload> NextTrace() => await _first.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
