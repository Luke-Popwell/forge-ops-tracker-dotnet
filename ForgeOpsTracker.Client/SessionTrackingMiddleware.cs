using Microsoft.AspNetCore.Http;

namespace ForgeOpsTracker;

/// <summary>
/// Counts every request as a session (crash-free unless an unhandled exception actually
/// escaped it) toward the crash-free rate on a project's Releases page. A separate,
/// independent middleware from ForgeOpsTrackerMiddleware, not layered onto it, since session
/// tracking runs regardless of whether error reporting is even configured: mirrors
/// gems/forge_ops_tracker's own split between its Rack middleware (sessions) and its
/// Rails.error hook (errors), two genuinely independent mechanisms.
///
/// Unlike Express, order relative to ForgeOpsTrackerMiddleware doesn't matter here: ASP.NET
/// Core middleware genuinely wraps the rest of the pipeline the way Rack's app.call(env) does
/// (this is why ForgeOpsTrackerMiddleware itself already gets away with a plain try/catch), so
/// this middleware observes the real exception either way. Register via
/// <c>app.UseForgeOpsTrackerSessionTracking()</c> (see ServiceCollectionExtensions).
/// </summary>
public sealed class SessionTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SessionFlusher _sessionFlusher;

    public SessionTrackingMiddleware(RequestDelegate next, SessionFlusher sessionFlusher)
    {
        _next = next;
        _sessionFlusher = sessionFlusher;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var entered = RequestState.Enter(context, _sessionFlusher.Configuration);
        var crashed = false;
        try
        {
            await _next(context);
            crashed = context.Response.StatusCode >= 500;
        }
        catch (Exception)
        {
            crashed = true;
            throw;
        }
        finally
        {
            _sessionFlusher.RecordSession(crashed);
            RequestState.Exit(entered);
        }
    }
}
