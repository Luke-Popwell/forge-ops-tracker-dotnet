using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// Ambient, non-DI entry point: for console apps, worker services, or
/// anything else that isn't wired up through ASP.NET Core's service
/// collection (see ServiceCollectionExtensions.AddForgeOpsTracker for the
/// DI-based path web apps should prefer instead). Named distinctly from
/// the <c>ForgeOpsTracker</c> namespace itself (rather than a bare
/// <c>ForgeOpsTracker.Configure(...)</c>, which the Ruby gem's module-level
/// <c>ForgeOpsTracker.configure</c> does) to avoid a class sharing its
/// enclosing namespace's name: the same reason error-tracking .NET SDKs commonly name
/// their static entry point <c>&lt;Vendor&gt;Sdk</c> rather than the bare vendor name.
/// </summary>
public static class ForgeOpsTrackerSdk
{
    private static readonly Lazy<Configuration> LazyConfiguration = new(() => new Configuration());

    // The error queue for the non-DI path, built on the first Capture. Building it (or the span queue
    // below) registers the exit drain: a ProcessExit hook that sends whatever is still queued in
    // either one (and waits for deliveries under way), for at most DeliveryQueue.ExitDrainTimeout
    // overall: without it a console app that captures an exception or finishes a trace and returns
    // from Main lost it, since the queues' workers die with the process. Only a process that captured
    // or traced something registers the hook, so nothing else ever waits at exit. A DI app's queues
    // are drained by the host's singleton disposal instead.
    //
    // A process killed by an unhandled exception never raises ProcessExit, which is exactly what the
    // documented `Capture(ex); throw;` pattern ends in, so the same drain also runs from
    // UnhandledException. That hook only sends what was already captured: it never reports the
    // unhandled exception itself, since this client leaves deciding what to report to each catch site.
    private static readonly Lazy<DeliveryQueue> LazyDeliveryQueue = new(() =>
    {
        var deliveryQueue = new DeliveryQueue(Configuration, new Client(Configuration, new HttpClient()));
        RegisterExitDrain();
        return deliveryQueue;
    });

    private static int _exitDrainRegistered;

    // One hook for both queues, registered by whichever is built first, so they share one budget at
    // exit rather than each taking its own. Reads the queues when the process exits, not when the
    // hook is registered, so a queue built later is drained too.
    private static void RegisterExitDrain()
    {
        if (Interlocked.Exchange(ref _exitDrainRegistered, 1) == 1) return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => DrainQueuesOnExit();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.IsTerminating) DrainQueuesOnExit();
        };
    }

    private static void DrainQueuesOnExit() =>
        DrainOnExit(
            Configuration,
            LazyDeliveryQueue.IsValueCreated ? LazyDeliveryQueue.Value : null,
            LazySpanQueue.IsValueCreated ? LazySpanQueue.Value : null);

    // The exit drain itself: blocks the exiting thread while both workers, still running on the
    // thread pool, send what's left side by side, for at most the shorter of
    // DeliveryQueue.ExitDrainTimeout and ShutdownTimeout in all. Returns whether both finished.
    internal static bool DrainOnExit(Configuration configuration, DeliveryQueue? errors, SpanQueue? spans)
    {
        var timeout = configuration.ShutdownTimeout < DeliveryQueue.ExitDrainTimeout ? configuration.ShutdownTimeout : DeliveryQueue.ExitDrainTimeout;
        var drains = new List<Task<bool>>();
        if (errors is not null) drains.Add(errors.DrainAsync(timeout));
        if (spans is not null) drains.Add(spans.DrainAsync(timeout));

        try
        {
            return Task.WhenAll(drains).GetAwaiter().GetResult().All(finished => finished);
        }
        catch (Exception ex)
        {
            configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] draining the queues at exit failed");
            return false;
        }
    }

    // Also warns (once) when nothing will be sent, for an app that configures through environment
    // variables alone and never calls Configure.
    private static readonly Lazy<ForgeOpsReporter> LazyReporter = new(() =>
    {
        Configuration.WarnOnceIfNotSending();
        return new ForgeOpsReporter(Configuration, new EventBuilder(Configuration), LazyDeliveryQueue.Value);
    });

    // The metrics reporter for the non-DI path (a console app or worker): built lazily on the first
    // capture, with a ProcessExit hook that flushes whatever is left, which is what a short-lived cron
    // program that captures a few readings and returns from Main relies on. A DI app injects
    // MetricsReporter instead, which AddForgeOpsTracker registers.
    private static readonly Lazy<MetricsReporter> LazyMetrics = new(() =>
    {
        var reporter = new MetricsReporter(Configuration, new Client(Configuration, new HttpClient()));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => reporter.FlushAsync(new CancellationTokenSource(Configuration.ShutdownTimeout).Token).GetAwaiter().GetResult();
        return reporter;
    });

    // The affected user set via SetUser below, if any: AsyncLocal, not a plain static field or a
    // ThreadLocal (see sdks/java's own ThreadLocal-based equivalent): ASP.NET Core request
    // handling hops across thread-pool threads at every await, which a ThreadLocal would not
    // survive, but an AsyncLocal correctly flows through, the direct .NET analog to Node's
    // AsyncLocalStorage for the same reason.
    //
    // Internal, not private: shared with the DI-based path too. ForgeOpsTrackerUserContextMiddleware
    // (see ServiceCollectionExtensions.UseForgeOpsTrackerUserContext) sets this same AsyncLocal
    // from context.User, and ForgeOpsTrackerMiddleware reads it when reporting an automatically-
    // caught exception, rather than the DI pipeline inventing a second, separate ambient-user
    // store: one async flow, one ambient user, regardless of which of this SDK's two entry points
    // (this static facade, or the DI-registered middleware) ultimately reports it.
    internal static readonly AsyncLocal<IDictionary<string, object?>?> CurrentUser = new();

    // The current request's breadcrumb trail, if any: a second, independent AsyncLocal instance
    // rather than folding this into CurrentUser above, the same "own storage, own wrapping" shape
    // gems/forge_ops_tracker's own Thread.current[:forge_ops_tracker_breadcrumbs] (separate from
    // Thread.current[:forge_ops_tracker_current_user]) and sdks/node's own second
    // AsyncLocalStorage instance (separate from userStorage) both already take, for the identical
    // reason: distinct concern, an ordered trail rather than a single attached value.
    //
    // Unlike Node's AsyncLocalStorage.run(), a plain AsyncLocal<T>.Value assignment does not
    // auto-unwind at the end of a scope: it flows forward to every child async continuation from
    // the point it's set, but nothing here makes it "pop" back to its prior value once whatever
    // logical scope set it is done. Confirmed directly against UserContextMiddleware above, which
    // already has to clear CurrentUser in an explicit finally for exactly this reason (see its own
    // doc comment), not because AsyncLocal "just handles it" the way AsyncLocalStorage.run() does
    // in Node. BreadcrumbContextMiddleware needs, and has, the identical explicit finally-clear;
    // see its own doc comment for why that clear is unconditional.
    internal static readonly AsyncLocal<BreadcrumbBuffer?> CurrentBreadcrumbs = new();

    // The current request's (or trace's) span buffer, and the id of the span currently open in this
    // async flow: two independent AsyncLocals, so parallel tasks fanned out from one request each
    // keep their own idea of "the parent span" while sharing one buffer.
    internal static readonly AsyncLocal<SpanBuffer?> CurrentTrace = new();
    internal static readonly AsyncLocal<string?> CurrentSpanId = new();

    // The current web request's RequestState (trace id, remote parent, transaction name, endpoint,
    // errored flag), published by whichever of this client's middlewares runs first; see
    // RequestState.Enter. Independent of CurrentTrace: every request has one, even with TrackTracing
    // off, since its trace id is also what links an error here to errors in other services.
    internal static readonly AsyncLocal<RequestState?> CurrentRequest = new();

    /// <summary>
    /// The current trace's id (32 lowercase hex characters): the web request's own when inside one,
    /// even with TrackTracing off, or the open <see cref="TraceAsync{T}"/> trace's; null outside
    /// both. Handy for your own logs.
    /// </summary>
    public static string? CurrentTraceId => CurrentRequest.Value?.TraceId ?? CurrentTrace.Value?.TraceId;

    // The trace queue for the non-DI path (TraceAsync), built on the first finished trace, with the
    // same exit drain as LazyDeliveryQueue above. A DI app's SpanQueue is disposed by the host.
    private static readonly Lazy<SpanQueue> LazySpanQueue = new(() =>
    {
        var spanQueue = new SpanQueue(Configuration, new Client(Configuration, new HttpClient()));
        RegisterExitDrain();
        return spanQueue;
    });

    public static Configuration Configuration => LazyConfiguration.Value;

    // The change tracker for the non-DI path, built on first use with a ProcessExit hook that drains
    // whatever is still queued, the same as LazyMetrics above. A DI app injects ChangeTracker instead.
    private static readonly Lazy<ChangeTracker> LazyChanges = new(() =>
    {
        var tracker = new ChangeTracker(Configuration, new Client(Configuration, new HttpClient()));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tracker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return tracker;
    });

    /// <summary>
    /// Applies <paramref name="configure"/>, then queues the startup change snapshot (once per
    /// process, sent from a background task, and only when enabled with DetectChanges on; see
    /// <see cref="ChangeTracker"/>). Configure never waits on it and never fails because of it.
    /// </summary>
    public static void Configure(Action<Configuration> configure)
    {
        configure(Configuration);
        Configuration.WarnOnceIfNotSending();

        try
        {
            if (Configuration.DetectChanges && Configuration.IsEnabled()) LazyChanges.Value.SendStartupSnapshot();
        }
        catch
        {
            // Startup must never fail because of the snapshot.
        }
    }

    /// <summary>
    /// Records one change (a feature flag flip, a config edit, a migration) using the ambient
    /// <see cref="Configuration"/>; see <see cref="ChangeTracker.RecordChange"/> for the arguments. In a
    /// DI app inject <see cref="ChangeTracker"/> instead. Never throws; a no-op when not enabled.
    /// </summary>
    public static bool RecordChange(
        string kind,
        string title,
        IDictionary<string, object?>? details = null,
        string? environment = null,
        string? service = null,
        string? actor = null,
        string? url = null,
        string? id = null,
        DateTimeOffset? occurredAt = null)
    {
        try
        {
            if (!Configuration.IsEnabled()) return false;
            return LazyChanges.Value.RecordChange(kind, title, details, environment, service, actor, url, id, occurredAt);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The affected user defaults to whatever <see cref="SetUser"/> last set for this async flow;
    /// breadcrumbs are always whatever <see cref="AddBreadcrumb"/> (or the automatic controller
    /// breadcrumb) accumulated on <see cref="CurrentBreadcrumbs"/> for this same async flow. Inside a
    /// web request the event also carries its trace id, transaction name, and endpoint (see
    /// ForgeOpsReporter.Report).
    /// </summary>
    public static void Capture(Exception exception, IDictionary<string, object?>? context = null, IDictionary<string, object?>? user = null) =>
        LazyReporter.Value.Report(exception, context, user ?? CurrentUser.Value, CurrentBreadcrumbs.Value?.All());

    /// <summary>
    /// Waits until every exception captured and every <see cref="TraceAsync{T}"/> trace finished so
    /// far has been sent, returning true once nothing is left or false if
    /// <paramref name="cancellationToken"/> fires first. A console app that returns from <c>Main</c>
    /// already gets this at exit (for up to 5 seconds); call it yourself before
    /// <c>Environment.FailFast</c>, or when you want delivery done before moving on. Never throws.
    /// </summary>
    public static async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        var errors = LazyDeliveryQueue.IsValueCreated ? LazyDeliveryQueue.Value.FlushAsync(cancellationToken) : Task.FromResult(true);
        var spans = LazySpanQueue.IsValueCreated ? LazySpanQueue.Value.FlushAsync(cancellationToken) : Task.FromResult(true);
        return await errors.ConfigureAwait(false) & await spans.ConfigureAwait(false);
    }

    /// <summary>
    /// Manually attaches an affected user to whatever gets reported for the rest of this async
    /// flow (a request, a background task): for a console app or worker service with no
    /// <c>HttpContext</c> to detect a user from at all, or to override what
    /// <c>ForgeOpsTrackerUserContextMiddleware</c> auto-detected for a web request (see
    /// ServiceCollectionExtensions.UseForgeOpsTrackerUserContext for the DI-based path's own
    /// automatic detection). Passing an empty dictionary (or null) clears whatever was set.
    /// </summary>
    public static void SetUser(IDictionary<string, object?>? user) =>
        CurrentUser.Value = user is null or { Count: 0 } ? null : user;

    /// <summary>
    /// Internal; called by PerformanceTrackingMiddleware's own automatic controller breadcrumb,
    /// never by host app code directly.
    ///
    /// Deliberately does NOT re-check Configuration.TrackBreadcrumbs itself, unlike
    /// gems/forge_ops_tracker's own ActiveSupport::Notifications subscriptions or sdks/node's own
    /// _recordBreadcrumb, both of which check their own single, unified Configuration instance at
    /// each automatic call site: this SDK has two Configuration instances that can coexist (the
    /// ambient <see cref="Configuration"/> lazy singleton above, and whatever instance a DI-based
    /// app registered via AddForgeOpsTracker), and PerformanceTrackingMiddleware only ever runs
    /// wired up through the DI path, so re-checking a flag here would mean guessing which of those
    /// two Configuration instances to trust, with no guarantee it's the same one that actually
    /// governed whether <see cref="CurrentBreadcrumbs"/> even has a buffer to add to.
    ///
    /// Instead, the gate is simply "does a buffer exist at all": BreadcrumbContextMiddleware (see
    /// its own doc comment) only ever creates one when its own injected Configuration's
    /// TrackBreadcrumbs is on, so a null CurrentBreadcrumbs.Value here already covers every case
    /// TrackBreadcrumbs=false is supposed to: the flag is off, or BreadcrumbContextMiddleware was
    /// never registered at all. A real design question the other ports don't answer for .NET,
    /// since none of them has two independently-configurable Configuration instances that could
    /// disagree with each other in the first place.
    /// </summary>
    internal static void RecordBreadcrumb(string message, string category, string level = "info", IDictionary<string, object?>? data = null) =>
        CurrentBreadcrumbs.Value?.Add(message, category, level, data);

    /// <summary>
    /// Adds one breadcrumb to the current request's trail (see BreadcrumbContextMiddleware for how
    /// a request gets its own buffer to start with), or, outside a request entirely (a console
    /// app, a background <see cref="Task"/> with no middleware installed at all), this call's own
    /// standalone trail for the rest of this async flow: lazily created the first time it's
    /// needed, the same "works standalone, no specific setup required" shape
    /// gems/forge_ops_tracker's own ForgeOpsTracker.add_breadcrumb and sdks/node's own
    /// addBreadcrumb both already have. Uses the ambient <see cref="Configuration"/> for that
    /// lazily-created buffer specifically (matching <see cref="Capture"/>'s own ambient-facade
    /// use of it), since there's no DI-registered Configuration to prefer when nothing has wired
    /// this call site up to a request at all.
    ///
    /// Works whether or not <see cref="Configuration.TrackBreadcrumbs"/> is on: that flag only
    /// gates the automatic controller breadcrumb <see cref="RecordBreadcrumb"/> above records,
    /// never this manual call, matching gems/forge_ops_tracker's own
    /// ForgeOpsTracker.add_breadcrumb.
    /// </summary>
    public static void AddBreadcrumb(string message, string category = "custom", string level = "info", IDictionary<string, object?>? data = null)
    {
        var buffer = CurrentBreadcrumbs.Value;
        if (buffer is null)
        {
            buffer = new BreadcrumbBuffer(Configuration);
            CurrentBreadcrumbs.Value = buffer;
        }

        buffer.Add(message, category, level, data);
    }

    /// <summary>
    /// Opens a span under whichever span is open in this async flow (or the trace's root); dispose
    /// it to record it, ideally with <c>using</c>. Returns a no-op scope outside a trace. Safe to
    /// dispose exactly once; a second dispose does nothing.
    /// </summary>
    public static IDisposable StartSpan(string name, string kind = "service", IDictionary<string, object?>? data = null) =>
        StartSpan(TraceParent.GenerateSpanId(), name, kind, data);

    // Opens a span under an id generated beforehand, as TracingHttpMessageHandler does so the
    // traceparent header it sends can name the span the call is later recorded as.
    internal static IDisposable StartSpan(string id, string name, string kind, IDictionary<string, object?>? data)
    {
        var trace = CurrentTrace.Value;
        if (trace is null) return NoopScope.Instance;

        var parentId = CurrentSpanId.Value ?? trace.RootSpanId;
        CurrentSpanId.Value = id;
        return new SpanScope(trace, id, parentId, name, kind, data);
    }

    /// <summary>Runs <paramref name="work"/> inside a span; records even when it throws, and rethrows unchanged.</summary>
    public static T Span<T>(string name, Func<T> work, string kind = "service", IDictionary<string, object?>? data = null)
    {
        using var scope = StartSpan(name, kind, data);
        return work();
    }

    public static async Task<T> SpanAsync<T>(string name, Func<Task<T>> work, string kind = "service", IDictionary<string, object?>? data = null)
    {
        using var scope = StartSpan(name, kind, data);
        return await work().ConfigureAwait(false);
    }

    /// <summary>Records a span you timed yourself under the current one; a no-op outside a trace.</summary>
    public static void RecordSpan(string name, string kind, DateTimeOffset startedAt, double durationMs, IDictionary<string, object?>? data = null)
    {
        var trace = CurrentTrace.Value;
        trace?.Record(TraceParent.GenerateSpanId(), CurrentSpanId.Value ?? trace.RootSpanId, name, kind, startedAt, durationMs, data);
    }

    /// <summary>
    /// Opens a <c>database</c> span carrying the SQL it runs, so ForgeOps can show which statement a
    /// slow request spent its time in. The statement is masked (every string and number replaced by
    /// <c>?</c>) and cut to 4000 characters when the span is recorded, then sent as
    /// <c>db.statement</c>; <paramref name="dbSystem"/> ("postgresql", "mysql", "sqlite", "mssql",
    /// "oracle", or any other name) goes out lowercased as <c>db.system</c>. Parameter values are never
    /// taken. A no-op scope outside a trace.
    /// </summary>
    public static IDisposable StartDatabaseSpan(string name, string? statement, string? dbSystem = null) =>
        StartSpan(name, "database", DatabaseSpanData(statement, dbSystem));

    /// <summary>Runs <paramref name="work"/> inside a database span carrying its SQL; see <see cref="StartDatabaseSpan"/>.</summary>
    public static T DatabaseSpan<T>(string name, string? statement, Func<T> work, string? dbSystem = null) =>
        Span(name, work, "database", DatabaseSpanData(statement, dbSystem));

    /// <summary>Awaits <paramref name="work"/> inside a database span carrying its SQL; see <see cref="StartDatabaseSpan"/>.</summary>
    public static Task<T> DatabaseSpanAsync<T>(string name, string? statement, Func<Task<T>> work, string? dbSystem = null) =>
        SpanAsync(name, work, "database", DatabaseSpanData(statement, dbSystem));

    /// <summary>Records a query you timed yourself as a database span carrying its SQL; a no-op outside a trace.</summary>
    public static void RecordDatabaseSpan(string name, string? statement, DateTimeOffset startedAt, double durationMs, string? dbSystem = null) =>
        RecordSpan(name, "database", startedAt, durationMs, DatabaseSpanData(statement, dbSystem));

    /// <summary>
    /// The data a database span carries: <c>db.statement</c> and <c>db.system</c>, each left out when
    /// null or blank. Add your own keys and pass it to <see cref="StartSpan(string, string, IDictionary{string, object?}?)"/>
    /// or <see cref="RecordSpan"/> with kind "database" if you prefer; a <c>db.statement</c> on any
    /// database span is masked when it's recorded, however the dictionary was built.
    /// </summary>
    public static IDictionary<string, object?> DatabaseSpanData(string? statement, string? dbSystem = null)
    {
        var data = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(statement)) data[SpanBuffer.DbStatement] = statement;
        if (!string.IsNullOrWhiteSpace(dbSystem)) data[SpanBuffer.DbSystem] = dbSystem.Trim().ToLowerInvariant();
        return data;
    }

    /// <summary>
    /// For code outside ASP.NET Core (a console app, a worker, a queue consumer): runs
    /// <paramref name="work"/> as a trace named <paramref name="rootName"/> using the ambient
    /// <see cref="Configuration"/>, sending it when it took at least TraceCaptureThreshold. A web
    /// app gets this automatically from PerformanceTrackingMiddleware. Inside an already-open trace
    /// it records a span instead.
    /// </summary>
    public static async Task<T> TraceAsync<T>(string rootName, Func<Task<T>> work)
    {
        if (CurrentTrace.Value is not null) return await SpanAsync(rootName, work).ConfigureAwait(false);
        if (!Configuration.TrackTracing || !Configuration.IsEnabled()) return await work().ConfigureAwait(false);

        var trace = new SpanBuffer(Configuration);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        CurrentTrace.Value = trace;
        CurrentSpanId.Value = trace.RootSpanId;
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            CurrentTrace.Value = null;
            CurrentSpanId.Value = null;
            var payload = trace.Finish(rootName, startedAt, stopwatch.Elapsed.TotalMilliseconds);
            if (payload is not null) LazySpanQueue.Value.Push(payload);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class SpanScope : IDisposable
    {
        private readonly SpanBuffer _trace;
        private readonly string _id;
        private readonly string _parentId;
        private readonly string _name;
        private readonly string _kind;
        private readonly IDictionary<string, object?>? _data;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _disposed;

        public SpanScope(SpanBuffer trace, string id, string parentId, string name, string kind, IDictionary<string, object?>? data)
        {
            _trace = trace;
            _id = id;
            _parentId = parentId;
            _name = name;
            _kind = kind;
            _data = data;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _stopwatch.Stop();
            CurrentSpanId.Value = _parentId;
            _trace.Record(_id, _parentId, _name, _kind, _startedAt, _stopwatch.Elapsed.TotalMilliseconds, _data);
        }
    }

    /// <summary>
    /// Records a named business metric (a signup, a payment, anything you want to name), buffered and
    /// flushed as one batch rather than one network call per capture. <paramref name="value"/> defaults
    /// to 1 for a bare counter; it may be negative (a refund). A no-op when the ambient
    /// <see cref="Configuration"/> isn't enabled for this environment. In a DI app inject
    /// <see cref="MetricsReporter"/> instead: it uses the configuration AddForgeOpsTracker built.
    /// </summary>
    public static void CaptureMetric(string name, double value = 1.0) => LazyMetrics.Value.CaptureMetric(name, value);

    /// <summary>Records one infrastructure reading from one of your own hosts; see <see cref="MetricsReporter.CaptureInfrastructureMetric"/>.</summary>
    public static void CaptureInfrastructureMetric(string name, double value, string? hostname = null) =>
        LazyMetrics.Value.CaptureInfrastructureMetric(name, value, hostname);

    /// <summary>Delivers every buffered metric and infrastructure reading right now.</summary>
    public static Task FlushMetricsAsync(CancellationToken cancellationToken = default) =>
        LazyMetrics.IsValueCreated ? LazyMetrics.Value.FlushAsync(cancellationToken) : Task.CompletedTask;
}
