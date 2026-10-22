using System.Diagnostics;

namespace ForgeOpsTracker;

/// <summary>
/// Records an "http" span for every outbound request made through an IHttpClientFactory client
/// while a trace is open, named "METHOD host" (never the path or query, which can carry ids or
/// tokens). Registered on every factory client by AddForgeOpsTracker; a no-op outside a request or
/// trace. Never alters the response or a thrown exception. Requests to this SDK's own ingestion
/// host and directory are skipped entirely, so delivering a trace can never record a span of its
/// own or carry a traceparent.
///
/// Also propagates the current trace to the service being called, as a W3C traceparent header (see
/// TraceParent, and Configuration.PropagateTraces/TracePropagationTargets for turning it off or
/// narrowing it to specific hosts). The header's parent id is this call's own span id, generated
/// before the call is made and then recorded with that exact id, so the downstream service's root
/// span points at a span that really exists in this trace. Inside a request the header goes out
/// even with TrackTracing off (no span is recorded then; the trace id alone still links errors
/// across services). A traceparent the app already set on the request is left alone, and so is one
/// on a request to a host outside TracePropagationTargets. .NET's own HttpClient diagnostics also
/// add a traceparent from Activity.Current when none is present; it never replaces one already set,
/// so the header this adds is the one that goes out.
/// </summary>
public sealed class TracingHttpMessageHandler : DelegatingHandler
{
    private readonly Configuration _configuration;

    public TracingHttpMessageHandler(Configuration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var traceId = ForgeOpsTrackerSdk.CurrentTraceId;
        if (traceId is null || IsOwnRequest(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var spanId = TraceParent.GenerateSpanId();
        Propagate(request, traceId, spanId);

        var name = $"{request.Method.Method} {request.RequestUri?.Host ?? "unknown"}";
        var data = new Dictionary<string, object?>();
        using var scope = ForgeOpsTrackerSdk.StartSpan(spanId, name, "http", data);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            data["status"] = (int)response.StatusCode;
            return response;
        }
        catch (Exception ex)
        {
            data["error"] = ex.GetType().Name;
            throw;
        }
    }

    // Never throws: failing to add a header must never stop the app's own request from going out.
    private void Propagate(HttpRequestMessage request, string traceId, string spanId)
    {
        try
        {
            if (request.Headers.Contains(TraceParent.Header) || !_configuration.ShouldPropagateTrace(request.RequestUri?.Host)) return;

            request.Headers.TryAddWithoutValidation(TraceParent.Header, TraceParent.Build(traceId, spanId));
        }
        catch (Exception)
        {
            // Swallowed deliberately; see above.
        }
    }

    private bool IsOwnRequest(Uri? uri)
    {
        var ingestion = _configuration.IngestionUri();
        if (uri is null || ingestion is null) return false;
        if (!string.Equals(uri.Host, ingestion.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != ingestion.Port) return false;

        var path = ingestion.AbsolutePath;
        var directory = path[..(path.LastIndexOf('/') + 1)];
        return uri.AbsolutePath.StartsWith(directory, StringComparison.Ordinal);
    }
}
