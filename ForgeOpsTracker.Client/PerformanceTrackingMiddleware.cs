using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ForgeOpsTracker;

/// <summary>
/// Times every request end to end and reports it, bucketed by transaction name, for a dashboard
/// widget on a project's Performance page (so it can show which parts of your app are actually
/// slow, not just which ones raise). A separate, independent middleware from
/// ForgeOpsTrackerMiddleware and SessionTrackingMiddleware, not layered onto either, for the same
/// reason those two are already split from each other: performance tracking runs regardless of
/// whether error reporting or session tracking are even configured. Mirrors
/// SessionTrackingMiddleware's own try/finally shape.
///
/// The transaction name is the matched endpoint's own route pattern (e.g. "/users/{id}"), read
/// from <see cref="HttpContext.GetEndpoint"/> after <c>_next(context)</c> runs, not the raw
/// request path: a distinct user id must not explode into its own separate transaction the way
/// the literal path would, the same low-cardinality reasoning every other SDK's own integration
/// already documents. The endpoint is only populated once ASP.NET Core's endpoint routing has
/// actually matched a route, which happens in UseRouting(); register this middleware with
/// <c>app.UseForgeOpsTrackerPerformanceTracking()</c> after <c>app.UseRouting()</c> (MVC's and
/// Minimal APIs' own default templates already call UseRouting() implicitly, so this is usually
/// automatic). Falls back to the raw <see cref="HttpRequest.Path"/> for a request that never
/// matched a route (a 404) or that ran before routing had a chance to match anything.
///
/// Also records a breadcrumb for the same request/controller lifecycle, gated independently of
/// TrackPerformance via ForgeOpsTrackerSdk.RecordBreadcrumb (see that method's own comment for
/// why it's the gate, not a TrackBreadcrumbs check here): an app could want the trail without the
/// timing data, or vice versa, and this is the only automatic breadcrumb source this client has
/// (there's no ORM/DB driver integration in this client to hang a query-level one off of, matching
/// the "don't invent one" instruction every SDK in this rollout was held to; see
/// sdks/PROGRESS.md). Mirrors gems/forge_ops_tracker/lib/forge_ops_tracker/railtie.rb's own
/// process_action.action_controller breadcrumb: same message shape ("METHOD transaction_name",
/// already computed below for the performance sample), category "controller", level "error" for a
/// 5xx response and "info" otherwise, data carrying status/path. One difference worth noting: for
/// a request an exception propagates out of, this finally block runs during unwind, before any
/// exception-handling middleware further up the pipeline has had a chance to turn that exception
/// into a response, so Response.StatusCode here typically still reads its pre-exception default
/// rather than whatever status the app eventually returns; a known, accepted gap, the same shape
/// sdks/python's own Django/Flask breadcrumb has for its own middleware-ordering reasons (see
/// sdks/PROGRESS.md's Python entry).
///
/// Each request's trace continues the caller's when the request arrived with a W3C traceparent
/// header (the root span is parented under the caller's span; see RequestState), and is sent when it
/// was slow or when the request errored: an exception escaping the pipeline, or anything reported
/// through ForgeOpsReporter while it ran.
/// </summary>
public sealed class PerformanceTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PerformanceFlusher _performanceFlusher;

    private readonly SpanQueue? _spanQueue;
    private readonly Configuration? _configuration;

    // spanQueue and configuration are optional so a middleware built by hand for performance
    // alone (no tracing) keeps working; DI always supplies both.
    public PerformanceTrackingMiddleware(RequestDelegate next, PerformanceFlusher performanceFlusher, SpanQueue? spanQueue = null, Configuration? configuration = null)
    {
        _next = next;
        _performanceFlusher = performanceFlusher;
        _spanQueue = spanQueue;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var entered = RequestState.Enter(context, _configuration);
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var trace = BeginTrace();
        try
        {
            await _next(context);
        }
        catch (Exception)
        {
            // Only observed on its way out, never handled.
            RequestState.MarkCurrentErrored();
            throw;
        }
        finally
        {
            stopwatch.Stop();
            var transactionName = RequestState.TransactionNameFor(context);
            _performanceFlusher.Record(transactionName, stopwatch.Elapsed.TotalMilliseconds);

            var status = context.Response.StatusCode;
            ForgeOpsTrackerSdk.RecordBreadcrumb(
                transactionName,
                category: "controller",
                level: status >= 500 ? "error" : "info",
                data: new Dictionary<string, object?> { ["status"] = status, ["path"] = context.Request.Path.Value });

            FinishTrace(trace, transactionName, startedAt, stopwatch.Elapsed.TotalMilliseconds);
            RequestState.Exit(entered);
        }
    }

    private SpanBuffer? BeginTrace()
    {
        if (_configuration is null || _spanQueue is null || !_configuration.TrackTracing || !_configuration.IsEnabled()) return null;

        var request = ForgeOpsTrackerSdk.CurrentRequest.Value;
        var trace = request is null
            ? new SpanBuffer(_configuration)
            : new SpanBuffer(_configuration, request.TraceId, request.ParentSpanId);
        ForgeOpsTrackerSdk.CurrentTrace.Value = trace;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = trace.RootSpanId;
        return trace;
    }

    private void FinishTrace(SpanBuffer? trace, string transactionName, DateTimeOffset startedAt, double durationMs)
    {
        ForgeOpsTrackerSdk.CurrentTrace.Value = null;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = null;
        if (trace is null) return;

        var payload = trace.Finish(transactionName, startedAt, durationMs, ForgeOpsTrackerSdk.CurrentRequest.Value?.Errored == true);
        if (payload is not null) _spanQueue!.Push(payload);
    }
}
