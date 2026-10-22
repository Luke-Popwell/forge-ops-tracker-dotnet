using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// Holds a single ForgeOps DSN plus everything else the client needs to
/// build and deliver events. Mirrors gems/forge_ops_tracker/lib/forge_ops_tracker/configuration.rb:
/// a single DSN string carries both the ingestion URL and the
/// project's api_key: "https://&lt;api_key&gt;@host/api/v1/events".
/// </summary>
public sealed class Configuration
{
    public string? Dsn { get; set; } = System.Environment.GetEnvironmentVariable("FORGE_OPS_DSN");

    /// <summary>
    /// FORGE_OPS_ENVIRONMENT, else ASPNETCORE_ENVIRONMENT, else DOTNET_ENVIRONMENT, else
    /// "Production" (what ASP.NET Core itself assumes when nothing is set). A blank value counts as
    /// unset.
    /// </summary>
    public string Environment { get; set; } = ResolveEnvironment(System.Environment.GetEnvironmentVariable);

    public string? Release { get; set; } = System.Environment.GetEnvironmentVariable("FORGE_OPS_RELEASE");

    public string? ServerName { get; set; } = SafeMachineName();

    // Used to decide whether a backtrace frame is "in_app": a frame is
    // in-app if its declaring type's assembly is in this set. Assembly
    // identity, not a file-path prefix (the Ruby gem's approach): a
    // compiled .NET assembly runs from its build output directory, while
    // a portable PDB's file info still points at the *original source*
    // path from whatever machine built it, so those two paths are
    // essentially never the same one. Defaults to just the entry
    // assembly; add your own additional assemblies explicitly (e.g. a
    // Domain or Application class library) if your app spans more than
    // one.
    public ISet<Assembly> AppAssemblies { get; set; } = DefaultAppAssemblies();

    public ISet<string> EnabledEnvironments { get; set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Production", "Staging" };

    public int QueueSize { get; set; } = 1000;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    // Bounds how long DeliveryQueue.DisposeAsync waits for the background
    // worker to drain on shutdown. The built-in Client can't itself hang
    // past Timeout above, but IClient is a public seam anyone can
    // implement: this keeps a misbehaving custom implementation from
    // being able to block app shutdown indefinitely.
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // See PiiScrubber: redacts likely-sensitive content (emails, credit
    // cards, known API key formats, anything under a suspiciously-named
    // key) before a payload ever leaves this process. ForgeOps itself
    // scrubs again on arrival regardless, so this is a second, earlier
    // layer, not the only one, but "on" is the only sane default.
    public bool ScrubPii { get; set; } = true;

    // Whether EventBuilder reads a few lines of source off disk around each in-app frame's
    // culprit line (see EventBuilder.AttachSourceContext). Defaults on so a snippet shows up
    // without any extra setup, but this flag alone isn't the real protection against literal
    // source code leaving this process: every ForgeOps project has its own server-side setting
    // that's the durable, enforced switch, since it applies no matter what this flag happens to
    // be set to on any given deployment. Turn this off here too if this host app should never
    // even attempt that disk read in the first place.
    public bool CaptureSourceContext { get; set; } = true;

    // When an error comes from a database call (Npgsql's PostgresException, and anything that
    // wraps one), send the names of the stored procedure, table and view its SQL touched, so an
    // issue says where to start looking. Names are identifiers, never values, which is why this
    // defaults on. CaptureSqlStatement is the separate, opt-in step of also sending the statement
    // itself, with every string and number replaced by "?"; off by default because even a masked
    // statement describes your schema, and ForgeOps' own per-project setting is what durably
    // governs whether the server stores it. See SqlStatement.
    public bool CaptureSqlObjects { get; set; } = true;

    public bool CaptureSqlStatement { get; set; }

    // Strict privacy, for apps that can't let personal or health data leave their own servers:
    // events carry only structured facts (exception class, stack frames, release, environment,
    // where it happened), never free text. The message is replaced by message_fingerprint (see
    // StrictPrivacy.MessageFingerprint), breadcrumbs aren't recorded or sent, endpoint,
    // sql_statement and source context aren't sent, the user is sent as its id only, and context
    // and tags keep only the PrivacyAllowedFields with plain values. Spans keep only their method
    // and host for outbound calls, and only db.statement and db.system as data. Off by default;
    // FORGE_OPS_STRICT_PRIVACY=true turns it on.
    public bool StrictPrivacy { get; set; } = ResolveStrictPrivacy(System.Environment.GetEnvironmentVariable);

    // The context and tag keys strict privacy lets through anyway (your own error codes, a feature
    // name), when their value is a string, number or boolean. Empty by default;
    // FORGE_OPS_PRIVACY_ALLOWED_FIELDS=error_code,feature sets it from the environment.
    public ISet<string> PrivacyAllowedFields { get; set; } = ResolvePrivacyAllowedFields(System.Environment.GetEnvironmentVariable);

    public ILogger? Logger { get; set; }

    // Whether SessionTrackingMiddleware counts every request as a session (crash-free unless
    // an unhandled exception actually escaped it) and periodically reports an aggregate
    // crash-free rate. On by default, the same "on unless you turn it off" posture error
    // tracking itself already has.
    public bool TrackSessions { get; set; } = true;

    // Interval between aggregate session reports; requests are counted in-process and flushed
    // as one small report on this interval, not one network call per request.
    public TimeSpan SessionFlushInterval { get; set; } = TimeSpan.FromSeconds(60);

    // Whether PerformanceTrackingMiddleware times every request, bucketed by transaction name,
    // and periodically reports the aggregates for a dashboard widget on a project's Performance
    // page. On by default, the same "on unless you turn it off" posture session tracking already
    // has.
    public bool TrackPerformance { get; set; } = true;

    // Interval between aggregate performance reports; same batching philosophy as
    // SessionFlushInterval, just bucketed by transaction name instead of one flat counter pair.
    public TimeSpan PerformanceFlushInterval { get; set; } = TimeSpan.FromSeconds(60);

    // Intervals between flushes of the buffered CaptureMetric / CaptureInfrastructureMetric entries
    // (see MetricBuffer). No TrackMetrics flag the way TrackPerformance has one: these are explicit
    // calls the host app's own code makes, not automatic instrumentation, so there is nothing to turn
    // off that simply not calling them doesn't already do.
    public TimeSpan MetricFlushInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan InfrastructureMetricFlushInterval { get; set; } = TimeSpan.FromSeconds(60);

    // Whether BreadcrumbContextMiddleware gives every request a fresh trail to accumulate into,
    // and PerformanceTrackingMiddleware's own automatic controller breadcrumb records into it. On
    // by default, the same "on unless you turn it off" posture every other automatic
    // instrumentation flag above already has. See BreadcrumbContextMiddleware for the per-request
    // buffer this gates, and ForgeOpsTrackerSdk.AddBreadcrumb for adding one by hand: that manual
    // API works regardless of this flag, so an app that only wants its own hand-added breadcrumbs,
    // with none of the automatic ones, turns this off and still gets AddBreadcrumb.
    public bool TrackBreadcrumbs { get; set; } = true;

    // Oldest entry dropped once a request's trail accumulates this many, the same ring-buffer cap
    // gems/forge_ops_tracker's own Configuration#max_breadcrumbs already documents, so a request
    // that runs a very large number of operations doesn't grow the payload unboundedly.
    public int MaxBreadcrumbs { get; set; } = 30;

    // Whether PerformanceTrackingMiddleware records a trace per request and reports it (when slow,
    // or when the request errored) to /spans. ForgeOpsTrackerSdk.StartSpan/Span only record inside
    // a trace, so turning this off stops every span. On by default, like every other automatic
    // instrumentation flag above. Every request still gets a trace id with this off: it goes on
    // errors reported during the request and on the traceparent header outbound calls carry (see
    // PropagateTraces), since that id is also what links an error here to one in another service.
    public bool TrackTracing { get; set; } = true;

    // A trace is only sent when its root span took at least this long (or its request errored).
    public TimeSpan TraceCaptureThreshold { get; set; } = TimeSpan.FromSeconds(1);

    // Whether outbound IHttpClientFactory calls made during a request carry a W3C traceparent header
    // (see TracingHttpMessageHandler), so the service being called continues this trace. On by
    // default, matching gems/forge_ops_tracker: the header carries the trace id that links an error
    // here to an error there, which is useful with or without spans, so it goes out even with
    // TrackTracing off. A traceparent the app set itself is never replaced, and this client's own
    // requests to ForgeOps never carry one.
    public bool PropagateTraces { get; set; } = true;

    // Whether a snapshot of what this process sees (the .NET runtime, plus environment variable
    // names when TrackEnvVarNames is on) is sent once at startup, so ForgeOps can record what changed
    // since the last deploy on its own. On by default; see ChangeTracker. RecordChange works either way.
    public bool DetectChanges { get; set; } = true;

    // Opt in to including environment variable NAMES in that snapshot (never values), so an added or
    // removed setting shows up as a change. Off by default. Host-specific noise such as HOSTNAME, PATH
    // and per-pod service discovery variables is always left out; see EnvVarNames.
    public bool TrackEnvVarNames { get; set; }

    // Which hosts get that header. Null (the default) means every host. Otherwise a list of host
    // strings, each matching that host and its subdomains on a dot boundary ("example.com" matches
    // "api.example.com", never "badexample.com"; case and a leading dot are ignored), and/or Regex
    // instances searched for anywhere in the lowercased host, so anchor them yourself. Anything else
    // in the list matches nothing. Useful for a third-party API that rejects unknown headers, or that
    // shouldn't learn your trace ids at all.
    public IList<object>? TracePropagationTargets { get; set; }

    /// <summary>Whether an outbound call to <paramref name="host"/> should carry a traceparent header; case-insensitive, since hostnames are.</summary>
    public bool ShouldPropagateTrace(string? host)
    {
        if (!PropagateTraces) return false;

        var targets = TracePropagationTargets;
        if (targets is null) return true;

        var normalized = (host ?? string.Empty).ToLowerInvariant();
        foreach (var target in targets)
        {
            switch (target)
            {
                case Regex regex when regex.IsMatch(normalized):
                    return true;
                case string text:
                    var domain = text.ToLowerInvariant().TrimStart('.');
                    if (domain.Length > 0 && (normalized == domain || normalized.EndsWith("." + domain, StringComparison.Ordinal))) return true;
                    break;
            }
        }

        return false;
    }

    public string? ApiKey
    {
        get
        {
            var userInfo = ParsedDsn()?.UserInfo;
            if (string.IsNullOrEmpty(userInfo)) return null;

            // UserInfo is "user:password" (or just "user") URL-encoded;
            // the DSN only ever carries the api_key as the user part, no
            // password.
            var user = userInfo.Split(':', 2)[0];
            return Uri.UnescapeDataString(user);
        }
    }

    /// <summary>
    /// The ingestion URL with credentials stripped out (they travel as the
    /// Authorization header instead, not embedded in the request URI).
    /// </summary>
    public Uri? IngestionUri()
    {
        var parsed = ParsedDsn();
        if (parsed is null) return null;

        var builder = new UriBuilder(parsed)
        {
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri;
    }

    /// <summary>
    /// Same derivation as <see cref="IngestionUri"/>, with the trailing "/events" swapped for
    /// "/session_checkins": one DSN, two endpoints, matching the Ruby gem's own
    /// Configuration#session_checkins_uri.
    /// </summary>
    public Uri? SessionCheckinsUri()
    {
        var uri = IngestionUri();
        if (uri is null) return null;

        var path = uri.AbsolutePath;
        if (!path.EndsWith("/events", StringComparison.Ordinal)) return uri;

        var builder = new UriBuilder(uri)
        {
            Path = path[..^"/events".Length] + "/session_checkins"
        };
        return builder.Uri;
    }

    /// <summary>
    /// Same derivation as <see cref="IngestionUri"/>, with the trailing "/events" swapped for
    /// "/performance_samples": one DSN, two more endpoints alongside SessionCheckinsUri, matching
    /// the Ruby gem's own Configuration#performance_samples_uri.
    /// </summary>
    public Uri? PerformanceSamplesUri()
    {
        var uri = IngestionUri();
        if (uri is null) return null;

        var path = uri.AbsolutePath;
        if (!path.EndsWith("/events", StringComparison.Ordinal)) return uri;

        var builder = new UriBuilder(uri)
        {
            Path = path[..^"/events".Length] + "/performance_samples"
        };
        return builder.Uri;
    }

    /// <summary>Same derivation as <see cref="IngestionUri"/>, swapping the trailing "/events" for "/custom_metrics".</summary>
    public Uri? CustomMetricsUri() => SwapEventsSuffix("/custom_metrics");

    /// <summary>Same derivation as <see cref="IngestionUri"/>, swapping the trailing "/events" for "/infrastructure_metrics".</summary>
    public Uri? InfrastructureMetricsUri() => SwapEventsSuffix("/infrastructure_metrics");

    private Uri? SwapEventsSuffix(string replacement)
    {
        var uri = IngestionUri();
        if (uri is null) return null;

        var path = uri.AbsolutePath;
        if (!path.EndsWith("/events", StringComparison.Ordinal)) return uri;

        return new UriBuilder(uri) { Path = path[..^"/events".Length] + replacement }.Uri;
    }

    /// <summary>Same derivation as <see cref="IngestionUri"/>, swapping the trailing "/events" for "/spans".</summary>
    public Uri? SpansUri()
    {
        var uri = IngestionUri();
        if (uri is null) return null;

        var path = uri.AbsolutePath;
        if (!path.EndsWith("/events", StringComparison.Ordinal)) return uri;

        return new UriBuilder(uri) { Path = path[..^"/events".Length] + "/spans" }.Uri;
    }

    /// <summary>Same derivation as <see cref="IngestionUri"/>, swapping the trailing "/events" for "/changes".</summary>
    public Uri? ChangesUri() => SwapEventsSuffix("/changes");

    /// <summary>Same derivation as <see cref="IngestionUri"/>, swapping the trailing "/events" for "/change_snapshots".</summary>
    public Uri? ChangeSnapshotsUri() => SwapEventsSuffix("/change_snapshots");

    /// <summary>The default <see cref="Environment"/>, read through <paramref name="variable"/>.</summary>
    /// <summary>FORGE_OPS_STRICT_PRIVACY: "true" or "1" (any case) turns strict privacy on.</summary>
    internal static bool ResolveStrictPrivacy(Func<string, string?> variable)
    {
        var value = variable("FORGE_OPS_STRICT_PRIVACY")?.Trim();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    /// <summary>FORGE_OPS_PRIVACY_ALLOWED_FIELDS: comma separated, blanks around each name ignored.</summary>
    internal static ISet<string> ResolvePrivacyAllowedFields(Func<string, string?> variable) =>
        new HashSet<string>(
            (variable("FORGE_OPS_PRIVACY_ALLOWED_FIELDS") ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

    internal static string ResolveEnvironment(Func<string, string?> variable)
    {
        foreach (var name in new[] { "FORGE_OPS_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
        {
            var value = variable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "Production";
    }

    private static int _environmentWarningGiven;

    /// <summary>
    /// The one line logged when a DSN is set but <see cref="Environment"/> isn't one of
    /// <see cref="EnabledEnvironments"/>, or null when there's nothing to say.
    /// </summary>
    internal string? NotSendingWarning()
    {
        if (string.IsNullOrWhiteSpace(Dsn) || EnabledEnvironments.Contains(Environment)) return null;

        var enabled = string.Join(", ", EnabledEnvironments.Select(name => name.ToLowerInvariant()).OrderBy(name => name, StringComparer.Ordinal));
        return $"[ForgeOps] Not sending: this environment is \"{Environment}\", and only {enabled} are enabled. "
            + $"Set FORGE_OPS_ENVIRONMENT=production (or add \"{Environment}\" to the enabled environments) to send from here.";
    }

    /// <summary>
    /// Says once per process, through <see cref="Logger"/> (stderr when there is none), that nothing
    /// will be sent: with a DSN set and an environment that doesn't send, every capture is silently
    /// dropped, which otherwise looks exactly like a broken setup. Called at init by both setup paths.
    /// </summary>
    internal void WarnOnceIfNotSending()
    {
        try
        {
            var warning = NotSendingWarning();
            if (warning is null || Interlocked.Exchange(ref _environmentWarningGiven, 1) == 1) return;

            if (Logger is not null) Logger.LogWarning("{Warning}", warning);
            else Console.Error.WriteLine(warning);
        }
        catch
        {
            // A warning must never break startup.
        }
    }

    /// <summary>@internal: lets a test see the once-per-process warning again.</summary>
    internal static void ResetEnvironmentWarningForTesting() => Interlocked.Exchange(ref _environmentWarningGiven, 0);

    public bool IsEnabled() =>
        !string.IsNullOrWhiteSpace(Dsn)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && EnabledEnvironments.Contains(Environment);

    private Uri? ParsedDsn()
    {
        if (string.IsNullOrWhiteSpace(Dsn)) return null;
        return Uri.TryCreate(Dsn, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string? SafeMachineName()
    {
        try
        {
            return System.Environment.MachineName;
        }
        catch
        {
            return null;
        }
    }

    private static ISet<Assembly> DefaultAppAssemblies()
    {
        var assemblies = new HashSet<Assembly>();
        var entry = Assembly.GetEntryAssembly();
        if (entry is not null) assemblies.Add(entry);
        return assemblies;
    }
}
