using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// Ties Configuration, EventBuilder, and DeliveryQueue together into the
/// one thing callers actually need: report an exception. Mirrors
/// gems/forge_ops_tracker/lib/forge_ops_tracker/error_subscriber.rb's
/// #report: never throws. An error reporter that itself raises while
/// reporting an error is the worst possible failure mode, so every path
/// here is wrapped to guarantee this never propagates back into the host
/// app.
///
/// Also says where the error happened: inside a web request (see RequestState, published by this
/// client's middleware) the event carries the request's trace_id, transaction_name, and endpoint,
/// and the request is marked as errored so its trace is sent however fast it was; inside a
/// ForgeOpsTrackerSdk.TraceAsync trace it carries that trace's trace_id. Read from the ambient
/// async flow here, unlike user/breadcrumbs (which callers pass in), so every path that reports an
/// error (the middleware, the ForgeOpsTrackerSdk.Capture facade, or an injected ForgeOpsReporter
/// called directly from a handler) gets them without passing anything new.
/// </summary>
public sealed class ForgeOpsReporter
{
    private readonly Configuration _configuration;
    private readonly IEventBuilder _eventBuilder;
    private readonly IDeliveryQueue _deliveryQueue;

    public ForgeOpsReporter(Configuration configuration, IEventBuilder eventBuilder, IDeliveryQueue deliveryQueue)
    {
        _configuration = configuration;
        _eventBuilder = eventBuilder;
        _deliveryQueue = deliveryQueue;
    }

    internal Configuration Configuration => _configuration;

    public void Report(Exception exception, IDictionary<string, object?>? context = null, IDictionary<string, object?>? user = null, IReadOnlyList<BreadcrumbEntry>? breadcrumbs = null)
    {
        try
        {
            if (!_configuration.IsEnabled()) return;

            var payload = _eventBuilder.Build(exception, context, user, breadcrumbs);
            var request = ForgeOpsTrackerSdk.CurrentRequest.Value;
            if (request is not null)
            {
                request.MarkErrored();
                payload = payload with
                {
                    TraceId = request.TraceId,
                    // Strict privacy leaves the endpoint out and gives the transaction name the span
                    // name rule (a raw path, the fallback when no route matched, loses its ids).
                    TransactionName = _configuration.StrictPrivacy && request.TransactionName is { } name
                        ? StrictPrivacy.SpanName(name)
                        : request.TransactionName,
                    Endpoint = _configuration.StrictPrivacy ? null : request.Endpoint
                };
            }
            else if (ForgeOpsTrackerSdk.CurrentTrace.Value is { } trace)
            {
                payload = payload with { TraceId = trace.TraceId };
            }

            _deliveryQueue.Push(payload);
        }
        catch (Exception ex)
        {
            _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] report failed");
        }
    }
}
