# ForgeOpsTracker.Client

.NET exception reporting client for [ForgeOps](https://getforgeops.net).
Targets `net8.0`. Captures unhandled exceptions automatically through ASP.NET Core middleware, lets
you report caught exceptions explicitly from anywhere in the app, and scrubs likely personal data
before anything leaves the process.

> For health or personal data, turn on [strict privacy](#strict-privacy): the SDK then sends only structured facts about each error (its type, stack trace, release and route), never the message, a raw path, breadcrumbs, or context you haven't allowed.

## Installation

```bash
dotnet add package ForgeOpsTracker.Client
```

## Configuration

Set a DSN (from a project's settings page in ForgeOps), either via the `FORGE_OPS_DSN` environment
variable or explicitly in code.

### ASP.NET Core apps (recommended)

```csharp
// Program.cs
builder.Services.AddForgeOpsTracker(config =>
{
    // "?? config.Dsn" keeps the FORGE_OPS_DSN environment variable when ForgeOps:Dsn isn't set:
    // a plain assignment would overwrite it with null.
    config.Dsn = builder.Configuration["ForgeOps:Dsn"] ?? config.Dsn;
    config.Release = builder.Configuration["ForgeOps:Release"] ?? config.Release;
});

var app = builder.Build();

app.UseForgeOpsTracker(); // early in the pipeline, before anything that would swallow exceptions first
```

This registers `Configuration`, `EventBuilder`, `DeliveryQueue`, and `ForgeOpsReporter` as
singletons (one shared background delivery queue per process, not one per request) and reports
every unhandled exception that reaches the middleware. The middleware only observes: it reports,
then re-throws the exact same exception, so your app's own exception handling and response
pipeline behave exactly as if it weren't there.

### Anywhere else (console apps, worker services, background jobs)

```csharp
ForgeOpsTrackerSdk.Configure(config =>
{
    config.Dsn = "https://<api_key>@getforgeops.net/api/v1/events";
});

try
{
    DoSomethingRisky();
}
catch (Exception ex)
{
    ForgeOpsTrackerSdk.Capture(ex, new Dictionary<string, object?> { ["job"] = "NightlyImport" });
    throw;
}
```

`ForgeOpsTrackerSdk` (not `ForgeOpsTracker`: that name is the namespace, not a type, and a type
can't share its enclosing namespace's name) is a lazily-initialized ambient singleton for anything
not wired through ASP.NET Core's service collection.

A console app or cron job that captures an exception and then exits straight away still gets it
delivered: once something has been captured, the static path sends whatever is still queued when
the process exits, whether `Main` returns or the exception is rethrown unhandled as above, waiting at
most 5 seconds (or `ShutdownTimeout`, if that's shorter), and `TraceAsync` traces go out within the
same 5 seconds. A process that never captured or traced anything doesn't wait. That exit hook only sends what you already captured; it never reports an unhandled
exception on its own. To be sure delivery is done before moving on, or before
`Environment.FailFast`, which skips it, await `ForgeOpsTrackerSdk.FlushAsync()`:

```csharp
ForgeOpsTrackerSdk.Capture(ex);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
await ForgeOpsTrackerSdk.FlushAsync(cts.Token); // true once everything has gone out
```

## What gets reported automatically, and what doesn't

**An exception that crashes a request needs no further wiring at all**: the middleware catches
anything that propagates all the way up through the pipeline and is never caught by your own code.

**An exception your own code catches and handles is a different story: the middleware never sees
it**, even inside a normal web request, because it never reaches the middleware in the first
place:

```csharp
app.MapPost("/charge", (Order order) =>
{
    try
    {
        ChargeCard(order);
    }
    catch (CardException ex)
    {
        logger.LogWarning("card declined: {Message}", ex.Message);
        // ForgeOps never sees this: caught locally, never propagates to the middleware.
        return Results.BadRequest("payment failed");
    }
    return Results.Ok();
});
```

.NET has no built-in, framework-level channel for reporting an exception your own code already
caught: report it explicitly instead, right at the catch site:

```csharp
catch (CardException ex)
{
    ForgeOpsTrackerSdk.Capture(ex, new Dictionary<string, object?> { ["order_id"] = order.Id });
    logger.LogWarning("card declined: {Message}", ex.Message);
    return Results.BadRequest("payment failed");
}
```

The same applies to anything running entirely outside a web request (a console app, a background
worker, a queue consumer): there's no middleware to catch anything there at all, so every catch
site that should be reported needs its own explicit `Capture` call, as in the example above.

Bottom line: if an exception would otherwise crash the request, you're already covered. If your own
code already catches it, add a `Capture` call at that specific catch site wherever you want
ForgeOps to know about it.

Reporting is a no-op outside `EnabledEnvironments` (`Production`/`Staging` by default) or without a
valid DSN configured. `Configuration.Environment` comes from `FORGE_OPS_ENVIRONMENT`, then
`ASPNETCORE_ENVIRONMENT`, then `DOTNET_ENVIRONMENT`, and is `Production` when none is set (what
ASP.NET Core itself assumes). With a DSN set and an environment that doesn't send, setup logs one
warning, once per process, through `Configuration.Logger` (the app's `ILogger` under
`AddForgeOpsTracker`; stderr when there is none):

```
[ForgeOps] Not sending: this environment is "Development", and only production, staging are enabled. Set FORGE_OPS_ENVIRONMENT=production (or add "Development" to the enabled environments) to send from here.
```

Delivery happens on a background task with a bounded channel and a short per-request HTTP timeout
(`Configuration.Timeout`, 2s default). Every failure mode: network errors, timeouts, a full queue,
a malformed DSN: is caught and dropped rather than thrown, so a broken or unreachable tracker can
never take down the host app.

## Identifying users

```csharp
ForgeOpsTrackerSdk.Capture(exception, user: new Dictionary<string, object?> { ["id"] = user.Id, ["email"] = user.Email });
```

Or `SetUser(IDictionary<string, object?>?)` to attach it for the rest of this async flow (a
request, a background task) rather than passing it to every `Capture` call by hand:

```csharp
ForgeOpsTrackerSdk.SetUser(new Dictionary<string, object?> { ["id"] = context.User.FindFirstValue(ClaimTypes.NameIdentifier) });
```

An `AsyncLocal<T>`, not a plain static field or a `ThreadLocal`: ASP.NET Core request handling
hops across thread-pool threads at every `await`, which a `ThreadLocal` would not survive, but an
`AsyncLocal` correctly flows through, the same reason Node's client needs `AsyncLocalStorage`
instead of a plain module-level variable. Passing an empty dictionary (or `null`) clears whatever
was set. Shows up on an issue's own detail page, and as its own affected-users count alongside the
regular event count.

**ASP.NET Core apps get this automatically**: add `app.UseForgeOpsTrackerUserContext()` (after
`app.UseAuthentication()`, so `context.User` is already populated) and it detects
`ClaimsPrincipal` claims for you, no per-app wiring needed:

```csharp
app.UseAuthentication();
app.UseForgeOpsTrackerUserContext();
app.UseForgeOpsTracker();
```

`ClaimTypes.NameIdentifier` for `id`, `ClaimTypes.Email` for `email`, `ClaimTypes.Name` for
`username`: the framework's own built-in claim types, populated by whatever auth handler is
configured (cookies, JWT bearer, ASP.NET Core Identity, ...), rather than any one third-party auth
library's own object shape, since ASP.NET Core has no single dominant one the way Rails has
Devise. A no-op for an unauthenticated request, or a custom auth handler that populates different
claim types; call `SetUser` yourself in that case. Composes with the manual API above rather than
replacing it: call `SetUser` yourself afterward (e.g. to override what was auto-detected) and it
wins for the rest of that request.

## `in_app` backtrace frames

A compiled .NET assembly runs from its build output directory, while a portable PDB's file info
still points at the original source path from whatever machine built it: those essentially never
match, so matching a frame's file path against a configured app root isn't a workable approach
here. `in_app` is instead determined by assembly identity: `Configuration.AppAssemblies` defaults
to just the entry assembly. If your app spans more than one project (a separate Domain or
Application class library, say), register those explicitly:

```csharp
config.AppAssemblies.Add(typeof(SomeTypeInYourDomainProject).Assembly);
```

File/line info on a frame requires a portable PDB deployed alongside the assembly to resolve at
all: on by default for Debug builds; add `<DebugType>portable</DebugType>` to your `.csproj` to
keep it in Release too, if you want that in production.

## Source context

By default, each in-app backtrace frame (never a third-party dependency) is captured along with
the 5 lines of source on either side of the culprit line, read straight off disk at capture time,
so an issue's detail page can show the actual code that broke, not just a file:line:method
reference. This never applies to a frame outside your own app's code, and it fails silently (no
context, not an error) for any file that can't be read: most production deployments of a compiled
language don't ship the original `.cs` files alongside the compiled assembly (and, as noted above,
resolving file/line at all already requires a portable PDB deployed alongside it), so this will
typically no-op in a Release deployment and only produce a snippet when source is actually present
on disk, such as during local development with a Debug build.

This is a real, deliberate exception to "off by default is safer": literal source code is being
transmitted, not just a reference to it, and the real protection here is not this flag. Every
project on ForgeOps has its own setting (on by default, off durably and immediately once an org
owner turns it off, regardless of what any individual app's own local setting is still set to)
that governs whether the server will ever actually store what this client sends. Set
`Configuration.CaptureSourceContext = false` if you'd rather this client never even attempt the
disk read in the first place:

```csharp
config.CaptureSourceContext = false;
```

## PII scrubbing

The message, backtrace, and any context/tags you attach are scanned for likely personal data
(email addresses, formatted SSNs/credit cards, known API key/token formats, and anything under a
suspiciously-named key like `password`, `apiKey`, or `ssn`) and redacted before the
payload ever leaves this process. ForgeOps itself scrubs again on arrival regardless, so this is a
second, earlier layer, not the only one. The user attached via `Capture`'s `user` parameter or
`SetUser` above is a deliberate exception: it's never scrubbed, since redacting it would defeat
the whole point of identifying users in the first place.

To disable it:

```csharp
config.ScrubPii = false;
```

## Strict privacy

For apps that can't let personal or health data (PII, PHI) leave their own servers. With strict
privacy on, an event carries only structured facts about the error, never free text that could
hold a name, an email or a diagnosis:

- The message isn't sent (it goes out as `""`). `message_fingerprint` goes instead: a one-way hash
  of the message (after PII scrubbing, when that's on) with its numbers, hex ids and UUIDs taken
  out, so ForgeOps still groups errors without seeing the text.
- Breadcrumbs aren't recorded or sent, and neither are `endpoint`, the SQL statement (even with
  `CaptureSqlStatement = true`) or source context.
- The user is sent as its `id` only, never `email` or `username`.
- Context and tags keep only the keys you allow, and only when the value is a string, number or
  boolean; each is left out when nothing is left.
- Outbound HTTP spans are named by method and host only (`GET api.example.com`), a request or span
  named by a raw path keeps only its first segment (`GET /patients/:path`, `/patients/:path`; a
  route template such as `GET /orders/{id}` is kept as it is), and span data keeps only
  `db.statement` (already masked) and `db.system`.

The exception class, stack frames, release, environment, server name, transaction name, trace id
and SQL object names are sent as before. Performance samples, session counts, metrics and changes
are unchanged: they're already counts and timings.

```csharp
builder.Services.AddForgeOpsTracker(config =>
{
    config.Dsn = builder.Configuration["ForgeOps:Dsn"] ?? config.Dsn;
    config.StrictPrivacy = true;
    config.PrivacyAllowedFields = new HashSet<string> { "error_code", "feature" };
});
```

Or from the environment: `FORGE_OPS_STRICT_PRIVACY=true` and
`FORGE_OPS_PRIVACY_ALLOWED_FIELDS=error_code,feature` (comma separated). An option set in code wins
over the environment. Both are off and empty by default.

ForgeOps applies the same rules again on arrival when the project's own Strict privacy setting is
on, so an app with an older or misconfigured client still can't get text stored for that project.

## Session tracking (release health)

By default, every request through the ASP.NET Core middleware is counted as a session: crash-free
unless an unhandled exception (or a 5xx response) actually affects it, giving ForgeOps a
crash-free rate per release to show alongside the errors themselves, not just the errors on their
own. Counted in-process and flushed as a small periodic aggregate on a background task (never one
network call per request), the same delivery philosophy as everything else in this client: a
broken or unreachable tracker never affects the host app either way. Registered as a DI singleton
implementing `IAsyncDisposable`, so the host's normal shutdown flushes whatever's left on a clean
exit, with no extra hosted-service wiring needed.

```csharp
builder.Services.AddForgeOpsTracker(config =>
{
    config.Dsn = builder.Configuration["ForgeOps:Dsn"] ?? config.Dsn;
    config.TrackSessions = false; // opt out entirely
    config.SessionFlushInterval = TimeSpan.FromSeconds(30); // default 60 seconds
});

var app = builder.Build();

app.UseForgeOpsTracker();
app.UseForgeOpsTrackerSessionTracking();
```

A separate middleware from `UseForgeOpsTracker`, registered alongside it rather than folded into
it, since session tracking runs regardless of whether error reporting is even configured. Requires
a ForgeOps plan that includes release health; on a plan that doesn't, the periodic flushes are
accepted but not recorded, and the response says why.

## Performance monitoring

By default, every request through the ASP.NET Core middleware is also timed end to end and
reported, bucketed by transaction name, so a dashboard widget on a project's Performance page can
show which parts of your app are actually slow, not just which ones raise. Counted in-process and
flushed as a small periodic aggregate the same way session checkins are, never one network call
per request.

Each aggregate also carries a small latency histogram (a count per fixed latency bucket: 50, 100,
250, 500, 1000, 2500, 5000 and 10000ms, plus an overflow bucket), so ForgeOps can show an
approximate p50/p95/p99 per transaction, not just an average. Percentiles are accurate to the width
of whichever bucket a duration falls into; the SDK never stores the individual durations.

```csharp
builder.Services.AddForgeOpsTracker(config =>
{
    config.Dsn = builder.Configuration["ForgeOps:Dsn"] ?? config.Dsn;
    config.TrackPerformance = false; // opt out entirely
    config.PerformanceFlushInterval = TimeSpan.FromSeconds(30); // default 60 seconds
});

var app = builder.Build();

app.UseRouting();
app.UseForgeOpsTracker();
app.UseForgeOpsTrackerSessionTracking();
app.UseForgeOpsTrackerPerformanceTracking(); // must come after UseRouting()

app.MapControllers(); // or MapGet/MapPost/etc. for Minimal APIs
```

The transaction name is the matched endpoint's own route pattern (e.g. `GET /users/{id}`), not the
raw request path, so a distinct user id doesn't explode into its own separate transaction; this is
why `UseForgeOpsTrackerPerformanceTracking()` needs to run after `UseRouting()` has had a chance
to match the request to an endpoint. A request that never matched a route (a 404) falls back to
the raw path instead. Requires a ForgeOps plan that includes performance monitoring; on a plan
that doesn't, the periodic flushes are accepted but not recorded, and the response says why.

## Breadcrumbs

A trail of what happened right before an error, on by default, no setup needed: every request
through the ASP.NET Core middleware is recorded automatically, and shows up alongside the error on
an issue's own detail page.

```csharp
builder.Services.AddForgeOpsTracker(config =>
{
    config.Dsn = builder.Configuration["ForgeOps:Dsn"] ?? config.Dsn;
    config.TrackBreadcrumbs = false; // opt out of the automatic source entirely
    config.MaxBreadcrumbs = 30; // oldest entry dropped once this many have accumulated in one request
});

var app = builder.Build();

app.UseRouting();
app.UseForgeOpsTrackerBreadcrumbs(); // first, before every other ForgeOpsTracker middleware
app.UseForgeOpsTracker();
app.UseForgeOpsTrackerPerformanceTracking(); // must come after UseRouting()
```

Add your own by hand, regardless of whether the automatic source is on:

```csharp
ForgeOpsTrackerSdk.AddBreadcrumb("charged card", category: "billing", data: new Dictionary<string, object?> { ["order_id"] = order.Id });
```

`category` defaults to `"custom"`, `level` to `"info"` (`"debug"`/`"info"`/`"warning"`/`"error"` are
the four levels the automatic source itself uses too), and `data` to an empty dictionary. Works
outside a request entirely too (a console app, a background worker): the trail it adds to is
created lazily on whatever async flow calls it, the same "works standalone, no specific setup
required" shape `ForgeOpsTrackerSdk.SetUser` already has, rather than silently doing nothing with
no `BreadcrumbContextMiddleware` around it.

Each request gets its own fresh, bounded trail (a ring buffer capped at `MaxBreadcrumbs`, oldest
entry dropped once full): scoped with a second `AsyncLocal<T>`, the identical mechanism
`ForgeOpsTrackerSdk.CurrentUser` already uses for the affected user (see "Identifying users"
above), so one request's trail never bleeds into another's on a reused thread-pool thread. Unlike
the affected user above, a breadcrumb's `message`/`data` **is** scrubbed for likely PII:
request-trail entries are exactly the kind of free text (a caller-built message, a data bag copied
from arbitrary request state) the scrubber exists to catch, not a deliberately-structured field the
way `user` is.

The one automatic source this client has today is the ASP.NET Core request/controller lifecycle
(the same one `UseForgeOpsTrackerPerformanceTracking` times): one breadcrumb per request, message
`"METHOD transaction_name"`, category `"controller"`, level `"error"` for a 5xx response and
`"info"` otherwise. Gated on `TrackBreadcrumbs` independently of `TrackPerformance`: an app can want
the trail without the timing data, or vice versa.

`AsyncLocal<T>.Value`, unlike `SetUser`'s own values which flow forward through an `await` the same
way, does not automatically un-flow at the end of a request the way some other languages' own
per-request context managers do: `BreadcrumbContextMiddleware` clears it in an explicit `finally`
around the rest of the pipeline, the same reasoning `UserContextMiddleware` already documents for
`CurrentUser`, so a reused thread-pool thread never leaks one request's trail into the next.

## Distributed tracing

A slow request's own breakdown: which database calls, outbound HTTP calls, or pieces of your code
the time went to, shown as a span tree on ForgeOps. On by default, no setup beyond what
`UseForgeOpsTrackerPerformanceTracking()` already needs: that middleware starts a trace for every
request and, once it finishes, sends it only when the request took at least `TraceCaptureThreshold`
(1 second by default) or when it errored, so fast, successful requests cost nothing on the wire. A
trace follows a request across services; see "Following a request across services" below.

**Automatic:** the request itself (the root span, named like the performance transaction) and every
outbound call made through an `IHttpClientFactory` client (`AddHttpClient`, typed clients), recorded
as an `http` span named `METHOD host`, never the path or query. There is no ORM or database driver
integration in this client, so query spans are manual.

```csharp
var order = await ForgeOpsTrackerSdk.SpanAsync("load order", () => repo.FindAsync(id), kind: "database",
    data: new Dictionary<string, object?> { ["orderId"] = id });
var total = ForgeOpsTrackerSdk.Span("price", () => Price(order)); // kind defaults to "service"

using (ForgeOpsTrackerSdk.StartSpan("render", "service")) { Render(order); }

// Something you timed yourself (kind is one of controller/service/database/redis/http/job/other):
ForgeOpsTrackerSdk.RecordSpan("SELECT orders", "database", startedAt, durationMs);
```

A span nests under whichever span is open in the same async flow, tracked in an `AsyncLocal`, so
parallel tasks (`Task.WhenAll`) each parent under their own span while sharing one trace. `Span`
records even when the work throws, rethrowing it unchanged, and does nothing outside a trace.
Outside ASP.NET Core (a console app, a worker) wrap the unit of work in
`ForgeOpsTrackerSdk.TraceAsync("job:reindex", () => ...)`, which uses the ambient configuration. A
console app that returns from `Main` right after a trace still sends it: the same exit hook that
sends captured exceptions sends queued traces too, within the same 5 seconds (or `ShutdownTimeout`),
and `ForgeOpsTrackerSdk.FlushAsync()` waits for both. A trace holds at most 500 spans. Turn span
reporting off with `TrackTracing = false`.

**Database spans with their SQL.** Wrap a query in `DatabaseSpanAsync` (or `DatabaseSpan`,
`StartDatabaseSpan`) and pass its SQL, and ForgeOps shows which statement a slow request spent its
time in:

```csharp
using ForgeOpsTracker;
using Npgsql;

const string OpenOrders = "SELECT id FROM orders WHERE customer_id = @customer AND status = 'open'";

app.MapGet("/orders/{customerId:long}", async (long customerId, NpgsqlDataSource db) =>
    await ForgeOpsTrackerSdk.DatabaseSpanAsync("Load open orders", OpenOrders, async () =>
    {
        await using var command = db.CreateCommand(OpenOrders);
        command.Parameters.AddWithValue("customer", customerId);
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids;
    }, dbSystem: "postgresql"));
```

The statement is masked before it's stored: every string and number becomes `?`, so this span
carries `SELECT id FROM orders WHERE customer_id = @customer AND status = ?`, and ForgeOps masks it
again on arrival. It's cut to 4000 characters, and parameter values are never read. It goes out in
the span's data as `db.statement`, with the database name (`"postgresql"`, `"mysql"`, `"sqlite"`,
`"mssql"`, `"oracle"`, or any other; optional) lowercased as `db.system`. For a query you timed
yourself, use `ForgeOpsTrackerSdk.RecordDatabaseSpan(name, sql, startedAt, durationMs, dbSystem)`.
`ForgeOpsTrackerSdk.DatabaseSpanData(sql, dbSystem)` builds the data dictionary if you'd rather add
your own keys and call `Span`/`RecordSpan` with kind `"database"`; a `db.statement` on any database
span is masked the same way, however the dictionary was built.

Masking covers backslash-escaped quotes (`'o\'brien'`), prefixed strings (`E'...'`, `X'DEADBEEF'`,
`N'...'`, `B'...'`, `U&'...'`), and hex, binary, exponent and leading-dot numbers (`0x1F`, `0b101`,
`1.5E-3`, `.5`), never the digits inside a name like `orders2` or a `$1` placeholder. With
`dbSystem` `"mysql"` or `"mariadb"`, double-quoted text is masked too, since those databases read
`"..."` as a string; for any other database it's a name and is left as is.

### Following a request across services

Traces use the [W3C Trace Context](https://www.w3.org/TR/trace-context/) standard (a `traceparent`
header), so an error or a slow request can be followed from one service into the next. Nothing to
set up beyond the middleware above:

- **Incoming**: every request that passes through any of this client's middlewares
  (`UseForgeOpsTracker`, `UseForgeOpsTrackerPerformanceTracking`, `UseForgeOpsTrackerBreadcrumbs`,
  `UseForgeOpsTrackerSessionTracking`) gets a trace id. A request that arrives with a valid
  `traceparent` header continues that trace: same trace id, and its root span nests under the
  caller's span. Otherwise it uses the trace id of ASP.NET Core's own request `Activity` when there
  is one (so a plain `new HttpClient()`, which propagates that `Activity` by itself, sends the same
  trace id), or a fresh one.
- **Outgoing**: every call through an `IHttpClientFactory` client made during a request (or inside
  `TraceAsync`) carries a `traceparent` header whose parent id is that call's own `http` span, so
  the next service's spans nest under it. A `traceparent` you set yourself is never replaced, and
  this client's own requests to ForgeOps never carry one.
- **Where an error happened**: every error reported during a request (an unhandled exception
  reaching `UseForgeOpsTracker`, `ForgeOpsTrackerSdk.Capture`, or an injected
  `ForgeOpsReporter.Report`) carries `trace_id`, `transaction_name` (the same name performance
  samples use), and `endpoint`: the HTTP method plus the matched route template, e.g.
  `GET /orders/{id}`, never the raw path, and left out when no route matched. These are read when
  the error is reported, so one reported from inside an endpoint already has its route. Errors
  outside a request are unchanged, apart from `trace_id` inside a `TraceAsync` trace.
  `ForgeOpsTrackerSdk.CurrentTraceId` returns the id for your own logs.
- A request that errored always sends its trace, however fast it was, so the issue page can show
  what led up to the error.

The trace id and the header exist even with `TrackTracing = false`, since they are also what links
errors across services; only span reporting stops. The service on the other end must also report
to ForgeOps, and both projects must be linked in ForgeOps to see their errors connected.

Narrow or turn off where the header goes, for example if a third-party API rejects unknown headers:

```csharp
builder.Services.AddForgeOpsTracker(config =>
{
    config.PropagateTraces = false; // never send traceparent (default true)

    // Default null: every host. A string matches that host and its subdomains on a dot boundary
    // ("example.com" matches "api.example.com", not "badexample.com"); a Regex is searched for in
    // the host, so anchor it yourself.
    config.TracePropagationTargets = new List<object> { "internal.example", new Regex(@"^10\.0\.") };
});
```

## Custom metrics and infrastructure monitoring

Two explicit calls (nothing is automatic, so there is no `TrackMetrics` flag): a business event you
name yourself, and a reading from one of your own hosts. In an ASP.NET Core app inject
`MetricsReporter` (`AddForgeOpsTracker` registers it, using the configuration you gave it); in a
console app or worker use the static `ForgeOpsTrackerSdk` methods, which use the ambient
configuration.

```csharp
metrics.CaptureMetric("signup");            // value defaults to 1: a bare counter
metrics.CaptureMetric("payment", 49);       // a real magnitude; it may be negative (a refund)

metrics.CaptureInfrastructureMetric("cpu", 0.42);                  // hostname defaults to ServerName
metrics.CaptureInfrastructureMetric("disk", 0.81, "db-1");
await metrics.FlushAsync();                                        // optional: send right now

ForgeOpsTrackerSdk.CaptureMetric("signup");                        // the same, without DI
```

Each capture is buffered and flushed as one batch every `MetricFlushInterval` /
`InfrastructureMetricFlushInterval` (60 seconds by default) on a `Task.Run` delay loop. In DI the
host's normal singleton disposal on shutdown flushes what is left; the static path registers an
`AppDomain.ProcessExit` hook, so a short-lived cron program that captures a few readings and returns
from `Main` needs nothing more. Every entry is stored as it was captured (a signup is a row, not a
running total), so a count or sum you compute later is exact. Both are a no-op when the client isn't
enabled for the environment.

A delivery that fails for now (a network error, a timeout, a 5xx, a 408 or a 429) keeps every entry
for the next flush, and an entry captured while a delivery is in flight is kept too (the Ruby gem's
own buffer loses it; a test pins this with a gated delivery). A batch the server rejects (any other
4xx, such as the 403 of a plan without the feature) is dropped and never sent again, and so is a batch
whose delivery has failed five flushes in a row. The buffer holds at most 1000 entries per kind and
drops further ones until a flush empties it.
A NaN or infinite value is dropped at capture: `System.Text.Json` refuses to write one, which would make
every batch behind it fail to send. Requires a ForgeOps plan that includes custom metrics /
infrastructure monitoring.

## Database errors

When an error comes from a database call, the event includes the names of the stored procedure, table and view its SQL touched, so the issue tells you where to start looking. This is on by default and sends identifiers only, never values. The statement is read from a `Statement`, `Sql` or `CommandText` property on the exception or anything it wraps (Npgsql's `PostgresException.Statement` is one), and from `exception.Data["forge_ops_sql"]`, which works for any exception type: providers such as `SqlException` expose nothing themselves, so attach it where you ran the query.

To also send the SQL statement itself, opt in. Every string and number is replaced by `?` before it
leaves your process (`WHERE email = 'a@b.co' AND id = 42` is sent as `WHERE email = ? AND id = ?`),
and ForgeOps masks it again on arrival. Escaped quotes, prefixed strings such as `X'DEADBEEF'`, and
hex, binary and exponent numbers are masked the same way:

```csharp
catch (Exception e)
{
    e.Data[SqlStatement.ExplicitDataKey] = query; // or "forge_ops_sql"
    throw;
}

// Opt in to also sending the masked statement (default false).
config.CaptureSqlStatement = true;
// config.CaptureSqlObjects = false; // default true; false stops even the names
```

Each ForgeOps project also has its own "Capture the SQL behind database errors" setting. Turn it off
there and the statement is never stored for that project, whatever this flag says; the names are
still kept. A view and a table are written the same way in SQL, so both show as tables/views; the
database's own error message usually settles which it was.

## Recording changes

Record a change (a feature flag flip, a config edit, a migration) and ForgeOps shows it alongside the
errors that followed, so "what changed?" has an answer. In an ASP.NET Core app inject `ChangeTracker`
(`AddForgeOpsTracker` registers it); anywhere else use `ForgeOpsTrackerSdk.RecordChange`, which uses
the ambient configuration.

```csharp
using ForgeOpsTracker;

ForgeOpsTrackerSdk.Configure(config =>
{
    config.Dsn = "https://<api_key>@getforgeops.net/api/v1/events";
    config.Environment = "Production";
    config.DetectChanges = true;      // default: send a startup snapshot (the .NET runtime version)
    config.TrackEnvVarNames = true;   // default false: also send environment variable names, never values
});

ForgeOpsTrackerSdk.RecordChange(
    ChangeKind.FeatureFlag,
    "Enabled new checkout for 10% of users",
    details: new Dictionary<string, object?> { ["flag"] = "new_checkout", ["rollout"] = 10 },
    actor: "luke@example.com",
    id: "new-checkout-10"); // optional: makes a retried call idempotent

// In a DI app, the same call on an injected ChangeTracker:
// changeTracker.RecordChange(ChangeKind.Migration, "AddIndexToOrders");
```

`kind` is one of `feature_flag`, `config`, `migration`, `dependency`, `infrastructure` or `other` (the
`ChangeKind` constants); anything else is sent as `other`. `title` is required and cut to 200
characters. `environment` defaults to `Configuration.Environment` and `occurredAt` to now. The call only
queues the change: a background task sends it, failures are swallowed (including the 403 a plan without
change tracking gets), and it's a no-op when the client isn't enabled for the environment. `details`
goes through the same PII scrubbing as event context.

Once per process, shortly after startup, the client also sends a snapshot of what it sees, and ForgeOps
records what changed since the previous one for the same environment. In DI a hosted service queues it
as the host starts; in a console app or worker, `ForgeOpsTrackerSdk.Configure` does. Neither waits for
it. The snapshot carries the .NET runtime version (for example `.net 8.0.11`); .NET has no reliable way
to list an app's package versions at runtime, so dependencies aren't reported. With `TrackEnvVarNames`
on it also carries environment variable names, minus host-specific ones (`HOSTNAME`, `PATH`, `HOME`,
`LC_*`, `KUBERNETES_*`, `*_SERVICE_HOST`, `*_SERVICE_PORT*`, this client's own `FORGE_OPS_*` and
similar) so a fleet of identical hosts doesn't look like it changes on every restart. Set
`DetectChanges = false` to turn the snapshot off; `RecordChange` keeps working.

## Running the tests

```bash
cd sdks/dotnet
dotnet test
```
