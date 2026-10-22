using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ForgeOpsTracker;

/// <summary>
/// Everything this client knows about the current web request that isn't already owned by one of
/// the older, single-purpose AsyncLocals (the user, the breadcrumb trail, the span buffer): which
/// trace it belongs to, which remote span called it (if any), what it's called, and whether it
/// errored. Mirrors gems/forge_ops_tracker's own RequestState. Published on
/// <see cref="ForgeOpsTrackerSdk.CurrentRequest"/> for the duration of the request by whichever of
/// this client's middlewares runs first (see <see cref="Enter"/>), so ForgeOpsReporter,
/// TracingHttpMessageHandler, and the request's own trace all agree on one trace id.
///
/// The trace id exists for every request, not only when span tracing is on: it's also what an error
/// event carries so ForgeOps can link it to errors reported by other services taking part in the
/// same trace.
///
/// TransactionName and Endpoint are read from the HttpContext each time they're asked for while
/// the request runs, not fixed when it starts: the matched route only exists once UseRouting() has
/// run, so reading on demand is what lets an error reported from inside the endpoint itself already
/// carry it. Once the request is over they're frozen (<see cref="Freeze"/>) and the HttpContext
/// reference dropped, since ASP.NET Core reuses HttpContext objects and a background task started
/// during the request still sees this state through its captured ExecutionContext.
/// </summary>
internal sealed class RequestState
{
    internal const string ItemKey = "ForgeOpsTracker.RequestState";

    private HttpContext? _context;
    private string? _transactionName;
    private string? _endpoint;
    private int _errored;

    internal RequestState(string traceId, string? parentSpanId, HttpContext? context)
    {
        TraceId = traceId;
        ParentSpanId = parentSpanId;
        _context = context;
    }

    public string TraceId { get; }

    /// <summary>The calling service's span this request runs under, or null when it started the trace itself.</summary>
    public string? ParentSpanId { get; }

    /// <summary>The same name performance samples and the request's root span use, e.g. "GET /users/{id}".</summary>
    public string? TransactionName => _context is { } context ? Read(() => TransactionNameFor(context)) : _transactionName;

    /// <summary>
    /// "&lt;HTTP method&gt; &lt;route pattern&gt;", e.g. "GET /users/{id}", from the matched endpoint's
    /// route; null until (or unless) routing has matched one, never the raw path with ids in it.
    /// </summary>
    public string? Endpoint => _context is { } context ? Read(() => EndpointFor(context)) : _endpoint;

    public bool Errored => Volatile.Read(ref _errored) == 1;

    public void MarkErrored() => Volatile.Write(ref _errored, 1);

    /// <summary>
    /// This request's state, memoized in HttpContext.Items so a re-execution of the pipeline for the
    /// same request (UseExceptionHandler, UseStatusCodePagesWithReExecute) keeps its trace id.
    /// Continues the caller's trace when the request arrived with a usable W3C traceparent header;
    /// otherwise uses the trace id ASP.NET Core's own request Activity already has (so an HttpClient
    /// that isn't built by IHttpClientFactory, which propagates that Activity by itself, sends the
    /// same trace id), or a fresh one when there's no such Activity.
    /// </summary>
    internal static RequestState For(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var existing) && existing is RequestState state) return state;

        var incoming = TraceParent.Parse(context.Request.Headers[TraceParent.Header].FirstOrDefault());
        state = incoming is not null
            ? new RequestState(incoming.TraceId, incoming.ParentSpanId, context)
            : new RequestState(AmbientTraceId() ?? TraceParent.GenerateTraceId(), null, context);
        context.Items[ItemKey] = state;
        return state;
    }

    /// <summary>
    /// Publishes this request's state unless one already is (an outer middleware of this client got
    /// there first). Returns the state this call published, to hand to <see cref="Exit"/> in a
    /// finally, or null. A no-op when <paramref name="configuration"/> is null or not enabled. Never
    /// throws.
    /// </summary>
    internal static RequestState? Enter(HttpContext context, Configuration? configuration)
    {
        try
        {
            if (configuration is null || ForgeOpsTrackerSdk.CurrentRequest.Value is not null || !configuration.IsEnabled()) return null;

            var state = For(context);
            ForgeOpsTrackerSdk.CurrentRequest.Value = state;
            return state;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Clears what <see cref="Enter"/> published; does nothing for null.</summary>
    internal static void Exit(RequestState? entered)
    {
        if (entered is null) return;

        entered.Freeze();
        ForgeOpsTrackerSdk.CurrentRequest.Value = null;
    }

    /// <summary>Marks the current request (if any) as errored, so its trace is sent however fast it was.</summary>
    internal static void MarkCurrentErrored() => ForgeOpsTrackerSdk.CurrentRequest.Value?.MarkErrored();

    /// <summary>
    /// "&lt;HTTP method&gt; &lt;route pattern&gt;" once endpoint routing has matched (e.g. "GET /users/{id}"),
    /// the raw request path otherwise: the name performance samples and the root span have always used.
    /// </summary>
    internal static string TransactionNameFor(HttpContext context)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        return $"{context.Request.Method} {pattern ?? context.Request.Path.Value}";
    }

    internal static string? EndpointFor(HttpContext context)
    {
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (pattern is null) return null;

        return $"{context.Request.Method} {(pattern.StartsWith('/') ? pattern : "/" + pattern)}";
    }

    internal void Freeze()
    {
        var context = _context;
        if (context is null) return;

        _transactionName = Read(() => TransactionNameFor(context));
        _endpoint = Read(() => EndpointFor(context));
        _context = null;
    }

    private static string? AmbientTraceId()
    {
        var activity = Activity.Current;
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C) return null;

        var traceId = activity.TraceId.ToHexString();
        return traceId.Length == 32 && traceId != new string('0', 32) ? traceId : null;
    }

    // Never throws: a misbehaving HttpContext must not stop an error from being reported.
    private static string? Read(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
