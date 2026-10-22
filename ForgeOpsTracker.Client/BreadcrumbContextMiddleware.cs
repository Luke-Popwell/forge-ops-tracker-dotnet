using Microsoft.AspNetCore.Http;

namespace ForgeOpsTracker;

/// <summary>
/// Gives every request a fresh, empty breadcrumb trail to accumulate into (via
/// ForgeOpsTrackerSdk.AddBreadcrumb, or PerformanceTrackingMiddleware's own automatic controller
/// breadcrumb), so one request's trail never bleeds into another's. Register via
/// <c>app.UseForgeOpsTrackerBreadcrumbs()</c> (see ServiceCollectionExtensions), FIRST, before
/// UseForgeOpsTrackerUserContext, UseForgeOpsTrackerPerformanceTracking,
/// UseForgeOpsTrackerSessionTracking, and UseForgeOpsTracker itself: any of those can record a
/// breadcrumb of its own or run host app code that calls AddBreadcrumb, so the trail has to
/// already exist by the time any of them runs, exactly the ordering
/// gems/forge_ops_tracker's own initializer comment and sdks/node's own Express/Fastify
/// integrations both document for the identical reason.
///
/// A sibling to SessionTrackingMiddleware/UserContextMiddleware/PerformanceTrackingMiddleware, not
/// folded into any of them: distinct concern, same "own file, own registration point" shape every
/// middleware in this client already takes.
///
/// Takes Configuration via constructor injection (a singleton, safe to inject directly into
/// middleware) rather than reading the ambient ForgeOpsTrackerSdk.Configuration lazy singleton:
/// this SDK has two Configuration instances that can coexist (that ambient one, used by the
/// non-DI ForgeOpsTrackerSdk facade for console apps/worker services, and whatever instance a
/// DI-based web app registered via AddForgeOpsTracker), and this middleware only ever runs in the
/// DI-registered pipeline, so it has to gate on the same Configuration instance that pipeline was
/// actually configured with, not a second, possibly-unconfigured one. See
/// ForgeOpsTrackerSdk.RecordBreadcrumb's own comment for how PerformanceTrackingMiddleware's
/// automatic controller breadcrumb avoids the identical ambiguity on its own end.
///
/// The finally-clear is unconditional, not gated on TrackBreadcrumbs, the same reasoning
/// UserContextMiddleware's own doc comment gives for CurrentUser: ASP.NET Core's thread pool is
/// reused across requests, and AddBreadcrumb's own lazy-creation path (see its doc comment) works
/// standalone regardless of this flag, so a manually-added trail could otherwise leak into a
/// later, unrelated request even with TrackBreadcrumbs off. A real bug this exact shape already
/// caught in sdks/php's own first draft (see sdks/PROGRESS.md's breadcrumbs entry): skipping the
/// clear whenever the flag was off would have leaked a manually-added trail across requests under
/// any long-lived-process hosting model, ASP.NET Core's own thread-pool reuse included.
/// </summary>
public sealed class BreadcrumbContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Configuration _configuration;

    public BreadcrumbContextMiddleware(RequestDelegate next, Configuration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var entered = RequestState.Enter(context, _configuration);
        if (_configuration.TrackBreadcrumbs && _configuration.IsEnabled())
        {
            ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = new BreadcrumbBuffer(_configuration);
        }

        try
        {
            await _next(context);
        }
        finally
        {
            ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = null;
            RequestState.Exit(entered);
        }
    }
}
