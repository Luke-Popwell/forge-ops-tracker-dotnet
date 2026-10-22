using Microsoft.AspNetCore.Http;

namespace ForgeOpsTracker;

/// <summary>
/// Reports any exception that reaches it, then re-throws unchanged:
/// this middleware only observes; it must never alter how the app's own
/// exception handling/response pipeline behaves. That mirrors
/// Rails.error.subscribe semantics exactly: subscribers are notified, they
/// don't handle the error. Register via
/// <c>app.UseForgeOpsTracker()</c> (see ServiceCollectionExtensions),
/// placed early in the pipeline: before any exception-handling
/// middleware that would otherwise swallow the exception before this one
/// ever sees it.
///
/// Also gives the request its trace context if no other middleware of this client already has (see
/// RequestState.Enter): the reported event carries the request's trace_id, transaction_name, and
/// endpoint, and the request is marked as errored so its trace is sent however fast it was. The
/// state is still published when the report happens whatever order this client's middlewares are
/// registered in, since whichever of them publishes it only clears it once everything inside it
/// has returned or thrown.
/// </summary>
public sealed class ForgeOpsTrackerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ForgeOpsReporter _reporter;

    public ForgeOpsTrackerMiddleware(RequestDelegate next, ForgeOpsReporter reporter)
    {
        _next = next;
        _reporter = reporter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var entered = RequestState.Enter(context, _reporter.Configuration);
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // ForgeOpsReporter.Report itself applies no ambient fallback of its own (it's the
            // low-level reporter, deliberately dumb about where "user"/"breadcrumbs" come from,
            // the same "Reporter never does ambient resolution itself" split every other SDK in
            // this repo takes): whatever ForgeOpsTrackerUserContextMiddleware (or
            // ForgeOpsTrackerSdk.SetUser, for non-HTTP code sharing this same async flow) last set
            // is read here explicitly, and likewise for whatever BreadcrumbContextMiddleware plus
            // ForgeOpsTrackerSdk.AddBreadcrumb (or the automatic controller breadcrumb) have
            // accumulated on CurrentBreadcrumbs for this same async flow.
            _reporter.Report(ex, BuildContext(context), ForgeOpsTrackerSdk.CurrentUser.Value, ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value?.All());
            throw;
        }
        finally
        {
            RequestState.Exit(entered);
        }
    }

    private static IDictionary<string, object?> BuildContext(HttpContext context) => new Dictionary<string, object?>
    {
        ["path"] = context.Request.Path.ToString(),
        ["method"] = context.Request.Method,
        ["query_string"] = context.Request.QueryString.ToString(),
        ["trace_id"] = context.TraceIdentifier
    };
}
