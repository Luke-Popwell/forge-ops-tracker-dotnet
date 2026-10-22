using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace ForgeOpsTracker;

/// <summary>
/// Automatically identifies the affected user for every error reported during a request, when
/// <c>context.User</c> is authenticated. Register via
/// <c>app.UseForgeOpsTrackerUserContext()</c> (see ServiceCollectionExtensions), after
/// <c>app.UseAuthentication()</c>: <c>HttpContext.User</c> is always a non-null
/// <see cref="ClaimsPrincipal"/> (ASP.NET Core's own default, an empty/unauthenticated one when no
/// auth middleware ever ran), but it only carries real claims once whatever authentication
/// handler is configured (cookies, JWT bearer, ASP.NET Core Identity, ...) has actually run and
/// populated it.
///
/// Unlike Rails/Devise or Django's own auth app, ASP.NET Core has no single dominant third-party
/// auth *library* to duck-type against: <see cref="ClaimsPrincipal"/>/<see cref="ClaimTypes"/> is
/// the framework's own built-in abstraction every standard auth handler (cookies, JWT, OAuth,
/// ASP.NET Core Identity) already produces, so this reads the three well-known claim types those
/// handlers populate by convention rather than a specific library's own user object shape:
/// <see cref="ClaimTypes.NameIdentifier"/> for id, <see cref="ClaimTypes.Email"/> for email,
/// <see cref="ClaimTypes.Name"/> for username. A custom auth handler that populates different
/// claim types just isn't detected; call <see cref="ForgeOpsTrackerSdk.SetUser"/> yourself in
/// that case, the same "duck-type, don't require a specific shape" no-op every other framework
/// auto-detection in this repo falls back to when its own convention doesn't apply.
///
/// Sets the same <see cref="ForgeOpsTrackerSdk.CurrentUser"/> AsyncLocal
/// <see cref="ForgeOpsTrackerMiddleware"/> reads and <see cref="ForgeOpsTrackerSdk.SetUser"/>
/// itself sets, so it composes with, rather than replaces, that manual API: application code can
/// still call <c>ForgeOpsTrackerSdk.SetUser(...)</c> itself afterward to override what was
/// auto-detected for that one request. Always clears it again in a finally: AsyncLocal values
/// don't leak across unrelated requests the way a plain static field or even a ThreadLocal could
/// (each async flow gets its own copy-on-write value), but clearing explicitly keeps the
/// lifetime obviously scoped to exactly this request rather than however this middleware's own
/// implementation happens to interact with AsyncLocal's copy semantics.
/// </summary>
public sealed class UserContextMiddleware
{
    private readonly RequestDelegate _next;

    public UserContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            ForgeOpsTrackerSdk.SetUser(BuildUser(context.User));
        }

        try
        {
            await _next(context);
        }
        finally
        {
            ForgeOpsTrackerSdk.SetUser(null);
        }
    }

    private static IDictionary<string, object?> BuildUser(ClaimsPrincipal principal)
    {
        var user = new Dictionary<string, object?>();

        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(id))
        {
            user["id"] = id;
        }

        var email = principal.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrEmpty(email))
        {
            user["email"] = email;
        }

        var username = principal.FindFirstValue(ClaimTypes.Name);
        if (!string.IsNullOrEmpty(username))
        {
            user["username"] = username;
        }

        return user;
    }
}
