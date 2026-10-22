using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// The DI-based setup path for ASP.NET Core apps: register with
/// <c>services.AddForgeOpsTracker(c => c.Dsn = "...")</c> in
/// Program.cs/Startup.cs, then <c>app.UseForgeOpsTracker()</c> (and, optionally,
/// <c>app.UseForgeOpsTrackerBreadcrumbs()</c>/<c>app.UseForgeOpsTrackerSessionTracking()</c>) in
/// the middleware pipeline. Configuration,
/// EventBuilder, DeliveryQueue, ForgeOpsReporter, SessionFlusher, and PerformanceFlusher are all
/// registered as singletons deliberately: one shared delivery queue/session flusher/performance
/// flusher and background worker per process, not one per request. DeliveryQueue, SessionFlusher,
/// and PerformanceFlusher all implement IAsyncDisposable, so the host's normal singleton disposal
/// on shutdown drains/flushes them automatically, with no extra IHostedService wiring needed.
/// The one hosted service registered here, ChangeSnapshotHostedService, exists only to queue the
/// startup change snapshot as the host starts (see ChangeTracker).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddForgeOpsTracker(this IServiceCollection services, Action<Configuration>? configure = null)
    {
        services.AddSingleton(sp =>
        {
            var configuration = new Configuration
            {
                Logger = sp.GetService<ILoggerFactory>()?.CreateLogger("ForgeOpsTracker")
            };
            configure?.Invoke(configuration);
            configuration.WarnOnceIfNotSending();
            return configuration;
        });

        services.AddHttpClient<Client>();
        services.AddSingleton<IClient>(sp => sp.GetRequiredService<Client>());
        services.AddSingleton<EventBuilder>();
        services.AddSingleton<IEventBuilder>(sp => sp.GetRequiredService<EventBuilder>());
        services.AddSingleton<DeliveryQueue>();
        services.AddSingleton<IDeliveryQueue>(sp => sp.GetRequiredService<DeliveryQueue>());
        services.AddSingleton<ForgeOpsReporter>();
        services.AddSingleton<SessionFlusher>();
        services.AddSingleton<PerformanceFlusher>();
        services.AddSingleton<SpanQueue>();
        services.AddSingleton<MetricsReporter>();
        services.AddSingleton<ChangeTracker>();
        services.AddHostedService<ChangeSnapshotHostedService>();
        services.AddTransient<TracingHttpMessageHandler>();
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder =>
                builder.AdditionalHandlers.Add(builder.Services.GetRequiredService<TracingHttpMessageHandler>())));

        return services;
    }

    public static IApplicationBuilder UseForgeOpsTracker(this IApplicationBuilder app) =>
        app.UseMiddleware<ForgeOpsTrackerMiddleware>();

    /// <summary>
    /// Register FIRST, before UseForgeOpsTrackerUserContext, UseForgeOpsTrackerPerformanceTracking,
    /// UseForgeOpsTrackerSessionTracking, and UseForgeOpsTracker itself: every one of those can
    /// record a breadcrumb of its own (PerformanceTrackingMiddleware's automatic controller
    /// breadcrumb) or run host app code that calls ForgeOpsTrackerSdk.AddBreadcrumb, so the trail
    /// has to already exist by the time any of them runs. See BreadcrumbContextMiddleware's own
    /// doc comment for the full reasoning.
    /// </summary>
    public static IApplicationBuilder UseForgeOpsTrackerBreadcrumbs(this IApplicationBuilder app) =>
        app.UseMiddleware<BreadcrumbContextMiddleware>();

    public static IApplicationBuilder UseForgeOpsTrackerSessionTracking(this IApplicationBuilder app) =>
        app.UseMiddleware<SessionTrackingMiddleware>();

    /// <summary>
    /// Register after app.UseRouting() (implicit in MVC's and Minimal APIs' own default
    /// templates) so the matched endpoint's route pattern is available for the transaction name;
    /// see PerformanceTrackingMiddleware's own doc comment for why.
    /// </summary>
    public static IApplicationBuilder UseForgeOpsTrackerPerformanceTracking(this IApplicationBuilder app) =>
        app.UseMiddleware<PerformanceTrackingMiddleware>();

    /// <summary>
    /// Register after app.UseAuthentication(), so context.User is already populated by whatever
    /// auth handler is configured; see UserContextMiddleware's own doc comment for exactly what
    /// it detects.
    /// </summary>
    public static IApplicationBuilder UseForgeOpsTrackerUserContext(this IApplicationBuilder app) =>
        app.UseMiddleware<UserContextMiddleware>();
}
