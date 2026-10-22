using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

/// <summary>
/// The W3C trace context contract: one trace id per request (continued from an incoming
/// traceparent), on every error reported during it along with its transaction name and endpoint,
/// propagated to outbound IHttpClientFactory calls with the call's own span as parent, and an
/// errored request's trace always sent. End to end through a real pipeline (TestServer) where it
/// matters.
/// </summary>
public class TracePropagationTests
{
    private const string IncomingTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string IncomingSpanId = "00f067aa0ba902b7";
    private const string Incoming = "00-" + IncomingTraceId + "-" + IncomingSpanId + "-01";

    [Fact]
    public void ParsesAValidHeader()
    {
        var context = TraceParent.Parse(Incoming)!;
        Assert.Equal(IncomingTraceId, context.TraceId);
        Assert.Equal(IncomingSpanId, context.ParentSpanId);
        Assert.Equal(IncomingTraceId, TraceParent.Parse(" 00-" + IncomingTraceId + "-" + IncomingSpanId + "-00 ")!.TraceId);
        Assert.Equal(IncomingTraceId, TraceParent.Parse("cc-" + IncomingTraceId + "-" + IncomingSpanId + "-01-future-fields")!.TraceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("00-" + IncomingTraceId + "-" + IncomingSpanId + "-01-extra")]
    [InlineData("ff-" + IncomingTraceId + "-" + IncomingSpanId + "-01")]
    [InlineData("00-00000000000000000000000000000000-" + IncomingSpanId + "-01")]
    [InlineData("00-" + IncomingTraceId + "-0000000000000000-01")]
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-" + IncomingSpanId + "-01")]
    [InlineData("00-" + IncomingTraceId + "-" + IncomingSpanId)]
    [InlineData("0-" + IncomingTraceId + "-" + IncomingSpanId + "-01")]
    public void RejectsUnusableHeaders(string? value)
    {
        Assert.Null(TraceParent.Parse(value));
    }

    [Fact]
    public void BuildsAndGeneratesW3CIds()
    {
        Assert.Equal(Incoming, TraceParent.Build(IncomingTraceId, IncomingSpanId));
        var traceId = TraceParent.GenerateTraceId();
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.Matches("^[0-9a-f]{16}$", TraceParent.GenerateSpanId());
        Assert.NotEqual(traceId, TraceParent.GenerateTraceId());
        Assert.Matches("^[0-9a-f]{32}$", new SpanBuffer(new Configuration()).TraceId);
    }

    [Fact]
    public void PropagatesToEveryHostByDefaultAndNowhereWhenOff()
    {
        var configuration = new Configuration();
        Assert.True(configuration.PropagateTraces);
        Assert.Null(configuration.TracePropagationTargets);
        Assert.True(configuration.ShouldPropagateTrace("api.example.com"));

        configuration.PropagateTraces = false;
        Assert.False(configuration.ShouldPropagateTrace("api.example.com"));
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("API.Example.com", true)]
    [InlineData("orders.internal.test", true)]
    [InlineData("10.0.4.2", true)]
    [InlineData("badexample.com", false)]
    [InlineData("example.com.evil.test", false)]
    [InlineData("192.10.0.1", false)]
    [InlineData(null, false)]
    public void TargetsMatchHostsOnADotBoundaryAndRegexesAnywhere(string? host, bool expected)
    {
        var configuration = new Configuration
        {
            TracePropagationTargets = new List<object> { "Example.com", ".internal.test", new Regex(@"^10\.0\."), 42, "" }
        };
        Assert.Equal(expected, configuration.ShouldPropagateTrace(host));
    }

    [Fact]
    public void AnEmptyTargetListPropagatesNowhere()
    {
        Assert.False(new Configuration { TracePropagationTargets = new List<object>() }.ShouldPropagateTrace("example.com"));
    }

    [Fact]
    public void AFreshTraceReusesTheAmbientActivitysW3CTraceId()
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var state = RequestState.For(new DefaultHttpContext());
        Assert.Equal(activity.TraceId.ToHexString(), state.TraceId);
        Assert.Null(state.ParentSpanId);
    }

    [Fact]
    public void AnIncomingHeaderWinsOverTheAmbientActivity()
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var context = new DefaultHttpContext();
        context.Request.Headers["traceparent"] = Incoming;
        var state = RequestState.For(context);
        Assert.Equal(IncomingTraceId, state.TraceId);
        Assert.Equal(IncomingSpanId, state.ParentSpanId);
        Assert.Same(state, RequestState.For(context));
    }

    [Fact]
    public void FreezingKeepsTheNamesAndLetsGoOfTheHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/orders/7";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("orders/{id}"), 0, null, null));
        var state = RequestState.For(context);
        Assert.Equal("GET /orders/{id}", state.Endpoint);

        state.Freeze();
        context.SetEndpoint(null);
        context.Request.Method = "POST";

        Assert.Equal("GET /orders/{id}", state.Endpoint);
        Assert.Equal("GET orders/{id}", state.TransactionName);
    }

    [Fact]
    public async Task AnUnhandledErrorCarriesTheIncomingTraceIdAndRouteAndSendsTheFastTrace()
    {
        var recorder = new Recorder();
        using var host = await BuildHost(recorder, endpoints =>
            endpoints.MapGet("/orders/{id}", string (string id) => throw new InvalidOperationException("checkout failed"))).StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/orders/42");
        request.Headers.TryAddWithoutValidation("traceparent", Incoming);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().SendAsync(request));

        var reported = Assert.Single(recorder.Events);
        Assert.Equal(IncomingTraceId, reported.TraceId);
        Assert.Equal("GET /orders/{id}", reported.TransactionName);
        Assert.Equal("GET /orders/{id}", reported.Endpoint);
        var json = JsonSerializer.Serialize(reported);
        Assert.Contains("\"trace_id\":\"" + IncomingTraceId + "\"", json);
        Assert.Contains("\"endpoint\":\"GET /orders/{id}\"", json);

        // Far under TraceCaptureThreshold (30s here), but it errored: sent anyway, nested under the caller's span.
        var trace = await recorder.NextTrace();
        Assert.Equal(IncomingTraceId, trace.TraceId);
        Assert.Equal(IncomingSpanId, trace.Spans.Single(s => s.Name == "GET /orders/{id}").ParentSpanId);
    }

    [Fact]
    public async Task AHandledErrorReportedMidRequestHasItsRouteAndStillSendsTheTrace()
    {
        var recorder = new Recorder();
        string? traceIdInside = null;
        using var host = await BuildHost(recorder, endpoints =>
            endpoints.MapGet("/users/{id}", (string id, ForgeOpsReporter reporter) =>
            {
                traceIdInside = ForgeOpsTrackerSdk.CurrentTraceId;
                reporter.Report(new InvalidOperationException("handled"));
                return "ok";
            })).StartAsync();

        var response = await host.GetTestClient().GetAsync("/users/7");
        Assert.True(response.IsSuccessStatusCode);

        var reported = Assert.Single(recorder.Events);
        Assert.Matches("^[0-9a-f]{32}$", traceIdInside!);
        Assert.Equal(traceIdInside, reported.TraceId);
        Assert.Equal("GET /users/{id}", reported.Endpoint);
        var trace = await recorder.NextTrace();
        Assert.Equal(traceIdInside, trace.TraceId);
        Assert.Null(trace.Spans.Single(s => s.Name == "GET /users/{id}").ParentSpanId);
    }

    [Fact]
    public async Task TheErrorMiddlewareRegisteredInsideTheOthersStillHasTheRequestContext()
    {
        var recorder = new Recorder();
        using var host = await BuildHost(recorder, endpoints =>
            endpoints.MapDelete("/carts/{id}", string (string id) => throw new ArgumentException("bad cart")), errorMiddlewareLast: true).StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Delete, "/carts/9");
        request.Headers.TryAddWithoutValidation("traceparent", Incoming);
        await Assert.ThrowsAsync<ArgumentException>(() => host.GetTestClient().SendAsync(request));

        var reported = Assert.Single(recorder.Events);
        Assert.Equal(IncomingTraceId, reported.TraceId);
        Assert.Equal("DELETE /carts/{id}", reported.Endpoint);
        Assert.Equal(IncomingTraceId, (await recorder.NextTrace()).TraceId);
    }

    [Fact]
    public async Task WithTrackTracingOffErrorsAndOutboundCallsStillCarryTheTraceIdButNoTraceIsSent()
    {
        var recorder = new Recorder();
        using var host = await BuildHost(recorder, endpoints =>
            endpoints.MapGet("/a", async (IHttpClientFactory factory) =>
            {
                await factory.CreateClient("outbound").GetAsync("https://orders.internal/things");
                throw new InvalidOperationException("boom");
            }), tracing: false).StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/a");
        request.Headers.TryAddWithoutValidation("traceparent", Incoming);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().SendAsync(request));

        Assert.Equal(IncomingTraceId, Assert.Single(recorder.Events).TraceId);
        Assert.StartsWith("00-" + IncomingTraceId + "-", Assert.Single(recorder.Outbound).TraceParent);
        await Task.Delay(200);
        Assert.Empty(recorder.Traces);
    }

    [Fact]
    public async Task OutboundCallsCarryATraceparentNamingTheirOwnRecordedSpan()
    {
        var recorder = new Recorder();
        using var host = await BuildHost(recorder, endpoints =>
            endpoints.MapPost("/checkout", async (IHttpClientFactory factory) =>
            {
                using var http = factory.CreateClient("outbound");
                await http.PostAsync("https://payments.internal/charge?card=1", new StringContent("{}"));

                var preset = new HttpRequestMessage(HttpMethod.Get, "https://ledger.internal/");
                preset.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
                await http.SendAsync(preset);

                await http.GetAsync("https://api.thirdparty.test/rates");
                await http.PostAsync("https://tracker.example.com/api/v1/spans", new StringContent("{}"));
                return "ok";
            }), threshold: TimeSpan.Zero, targets: new List<object> { "internal", "tracker.example.com" }).StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/checkout");
        request.Headers.TryAddWithoutValidation("traceparent", Incoming);
        Assert.True((await host.GetTestClient().SendAsync(request)).IsSuccessStatusCode);

        var byHost = recorder.Outbound.ToDictionary(o => o.Host);
        var header = byHost["payments.internal"].TraceParent!;
        var parts = header.Split('-');
        Assert.Equal("00", parts[0]);
        Assert.Equal(IncomingTraceId, parts[1]);
        Assert.Equal("01", parts[3]);

        // The app's own header is never replaced; a host outside the targets and this client's own
        // ingestion endpoint get none.
        Assert.Equal("00-11111111111111111111111111111111-2222222222222222-01", byHost["ledger.internal"].TraceParent);
        Assert.Null(byHost["api.thirdparty.test"].TraceParent);
        Assert.Null(byHost["tracker.example.com"].TraceParent);

        var trace = await recorder.NextTrace();
        var root = trace.Spans.Single(s => s.Name == "POST /checkout");
        var payments = trace.Spans.Single(s => s.Name == "POST payments.internal");
        Assert.Equal(parts[2], payments.SpanId);
        Assert.Equal(root.SpanId, payments.ParentSpanId);
        Assert.Equal("http", payments.Kind);
        Assert.Contains(trace.Spans, s => s.Name == "GET api.thirdparty.test");
        Assert.DoesNotContain(trace.Spans, s => s.Name.Contains("tracker.example.com"));
        Assert.DoesNotContain("card=1", JsonSerializer.Serialize(trace));
    }

    [Fact]
    public async Task PropagateTracesOffSendsNoHeader()
    {
        var recorder = new Recorder();
        using var host = await BuildHost(recorder, endpoints =>
            endpoints.MapGet("/x", async (IHttpClientFactory factory) =>
            {
                await factory.CreateClient("outbound").GetAsync("https://orders.internal/");
                return "ok";
            }), propagate: false).StartAsync();

        await host.GetTestClient().GetAsync("/x");

        Assert.Null(Assert.Single(recorder.Outbound).TraceParent);
    }

    [Fact]
    public async Task OutsideARequestOrTraceNothingIsAddedOrAttached()
    {
        var outbound = new List<string?>();
        var handler = new TracingHttpMessageHandler(NewConfiguration())
        {
            InnerHandler = new FakeHandler(request =>
            {
                outbound.Add(request.Headers.TryGetValues("traceparent", out var values) ? values.Single() : null);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })
        };
        using var http = new HttpClient(handler);
        await http.GetAsync("https://orders.internal/");
        Assert.Null(Assert.Single(outbound));

        var delivered = new List<TrackedEvent>();
        var configuration = NewConfiguration();
        new ForgeOpsReporter(configuration, new EventBuilder(configuration), new ListQueue(delivered)).Report(new InvalidOperationException("job"));
        var reported = Assert.Single(delivered);
        Assert.Null(reported.TraceId);
        Assert.Null(reported.TransactionName);
        Assert.Null(reported.Endpoint);
        var json = JsonSerializer.Serialize(reported);
        Assert.DoesNotContain("trace_id", json);
        Assert.DoesNotContain("endpoint", json);
    }

    [Fact]
    public async Task InsideAStandaloneTraceErrorsAndOutboundCallsUseItsTraceId()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        ForgeOpsTrackerSdk.CurrentTrace.Value = buffer;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = buffer.RootSpanId;
        string? header = null;
        var delivered = new List<TrackedEvent>();
        try
        {
            var handler = new TracingHttpMessageHandler(NewConfiguration())
            {
                InnerHandler = new FakeHandler(request =>
                {
                    header = request.Headers.GetValues("traceparent").Single();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                })
            };
            using var http = new HttpClient(handler);
            await http.GetAsync("https://orders.internal/");

            var configuration = NewConfiguration();
            new ForgeOpsReporter(configuration, new EventBuilder(configuration), new ListQueue(delivered)).Report(new InvalidOperationException("job"));
        }
        finally
        {
            ForgeOpsTrackerSdk.CurrentTrace.Value = null;
            ForgeOpsTrackerSdk.CurrentSpanId.Value = null;
        }

        Assert.StartsWith("00-" + buffer.TraceId + "-", header);
        Assert.Equal(buffer.TraceId, Assert.Single(delivered).TraceId);
        Assert.Null(delivered[0].Endpoint);
        var span = buffer.Finish("job", DateTimeOffset.UtcNow, 5000)!.Spans.Single(s => s.Kind == "http");
        Assert.Equal(header!.Split('-')[2], span.SpanId);
    }

    [Fact]
    public async Task NothingIsPublishedWhenTheClientIsNotEnabled()
    {
        var recorder = new Recorder();
        string? traceId = "unset";
        using var host = await BuildHost(recorder, endpoints =>
            endpoints.MapGet("/x", () =>
            {
                traceId = ForgeOpsTrackerSdk.CurrentTraceId;
                return "ok";
            }), enabled: false).StartAsync();

        await host.GetTestClient().GetAsync("/x");
        Assert.Null(traceId);
    }

    [Fact]
    public async Task AFastSuccessfulRequestStillSendsNoTrace()
    {
        var recorder = new Recorder();
        using var host = await BuildHost(recorder, endpoints => endpoints.MapGet("/ok", () => "ok")).StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.TryAddWithoutValidation("traceparent", Incoming);
        await host.GetTestClient().SendAsync(request);
        await Task.Delay(200);

        Assert.Empty(recorder.Traces);
    }

    private static Configuration NewConfiguration() => new()
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        Environment = "production",
        EnabledEnvironments = { "production" }
    };

    private static IHostBuilder BuildHost(
        Recorder recorder,
        Action<IEndpointRouteBuilder> configureEndpoints,
        TimeSpan? threshold = null,
        bool tracing = true,
        bool propagate = true,
        IList<object>? targets = null,
        bool errorMiddlewareLast = false,
        bool enabled = true) =>
        new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = enabled ? "https://key@tracker.example.com/api/v1/events" : null;
                    c.EnabledEnvironments.Add("Development");
                    c.TrackTracing = tracing;
                    c.TraceCaptureThreshold = threshold ?? TimeSpan.FromSeconds(30);
                    c.PropagateTraces = propagate;
                    c.TracePropagationTargets = targets;
                });
                services.AddSingleton<IClient>(recorder);
                services.AddSingleton<IDeliveryQueue>(new ListQueue(recorder.Events));
                services.AddHttpClient("outbound").ConfigurePrimaryHttpMessageHandler(() => new FakeHandler(request =>
                {
                    lock (recorder.Outbound)
                    {
                        recorder.Outbound.Add((request.RequestUri!.Host,
                            request.Headers.TryGetValues("traceparent", out var values) ? string.Join(",", values) : null));
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }));
            });
            webHost.Configure(app =>
            {
                if (!errorMiddlewareLast) app.UseForgeOpsTracker();
                app.UseForgeOpsTrackerBreadcrumbs();
                app.UseRouting();
                app.UseForgeOpsTrackerPerformanceTracking();
                if (errorMiddlewareLast) app.UseForgeOpsTracker();
                app.UseEndpoints(configureEndpoints);
            });
        });

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _respond(request);
    }

    private sealed class ListQueue : IDeliveryQueue
    {
        private readonly List<TrackedEvent> _delivered;

        public ListQueue(List<TrackedEvent> delivered) => _delivered = delivered;

        public bool Push(TrackedEvent payload)
        {
            lock (_delivered) _delivered.Add(payload);
            return true;
        }
    }

    private sealed class Recorder : IClient
    {
        private readonly TaskCompletionSource<TracePayload> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TrackedEvent> Events { get; } = new();

        public List<TracePayload> Traces { get; } = new();

        public List<(string Host, string? TraceParent)> Outbound { get; } = new();

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
