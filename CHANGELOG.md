# Changelog

## 0.18.0 (2026-10-20)

- Strict privacy: a raw path now keeps only its first segment, instead of only losing its digit segments, since a later segment can name a person even without a digit (`/patients/janedoe`). `GET /patients/jane-doe/12` is sent as `GET /patients/:path`, `/42/edit` as `/:id/:path`. A route template (any segment such as `:id`, `{id}`, `<int:id>`, `[slug]` or `*path`) is kept as it is, so `GET /v1/orders/{id}` is no longer sent as `GET /:id/orders/{id}`. Applies to span names and transaction names (events and performance samples), the same rule the server applies. New `StrictPrivacy.StrictPath`; `PathWithoutIds` still works (marked `[Obsolete]`) and now calls it.

## 0.17.0 (2026-10-20)

- Strict privacy, for apps that can't let personal or health data leave their own servers. New `Configuration.StrictPrivacy` (or `FORGE_OPS_STRICT_PRIVACY=true`) sends only structured facts: the message is sent as `""` and `message_fingerprint` goes instead, a hash of it (after PII scrubbing) with numbers and ids taken out, so errors still group; breadcrumbs aren't recorded or sent; `endpoint`, the SQL statement and source context aren't sent; the user is sent as its `id` only; and context and tags keep only the keys in `PrivacyAllowedFields` (or `FORGE_OPS_PRIVACY_ALLOWED_FIELDS=error_code,feature`) with a string, number or boolean value, and are left out when nothing is left. Outbound HTTP spans keep only method and host, a request or span named by a raw path has its ids taken out (`GET /patients/:id`, `/patients/:id`), and span data keeps only `db.statement` and `db.system`. Off by default; nothing changes unless it's turned on. ForgeOps applies the same rules on arrival when the project's own Strict privacy setting is on.
- `TrackedEvent.Context` and `TrackedEvent.Tags` are now nullable (null only under strict privacy, when nothing allowed is left), and `TrackedEvent.MessageFingerprint` is new.

## 0.16.0 (2026-10-13)

- A batch the server will never accept is no longer re-sent forever. Performance samples, custom and infrastructure metrics and session checkins used to keep a batch after any failed delivery and send it again, with more added, at every flush. A 4xx answer other than 408 and 429 (a wrong key, a plan without the feature, a batch the server can't read or that is too large) now drops the batch at once, since the same batch would only get the same answer. Before this, a rejected metric batch also stayed in the 1000-entry buffer, so once the buffer filled, every metric captured afterward was dropped too.
- A delivery that fails for now (a network error, a timeout, a 5xx, a 408 or a 429) still keeps the batch for the next flush, but only so long: after 5 failed flushes in a row the batch is dropped and the next one starts clean. A successful flush starts the count over. The 1000-entry cap on each metric buffer is unchanged.
- Each drop is logged once through `Configuration.Logger` at Debug level, for example `[ForgeOpsTracker] dropped 12 metrics: the server rejected it` or `... delivery failed 5 times in a row`, and a non-2xx answer is logged with its status (`delivery rejected: HTTP 403`, `delivery failed: HTTP 503`). Nothing about delivery throws.
- No API changes: every `IClient` method still returns `true` only for a 2xx, and errors, traces and changes are still sent once and never retried. A custom `IClient` can only answer `true` or `false`, so its `false` always counts as a failure for now: its batches are kept, then dropped after the fifth failed flush in a row.

## 0.15.0 (2026-10-02)

- **Behavior change: with no environment variable set, `Configuration.Environment` is now `"Production"`, not `"Development"`**, matching what ASP.NET Core itself assumes when nothing is set. It is now resolved from `FORGE_OPS_ENVIRONMENT` first, then `ASPNETCORE_ENVIRONMENT`, then `DOTNET_ENVIRONMENT`; a blank value counts as unset. A console app or service with a DSN and no environment variables now sends, where before every capture was silently dropped. A machine that sets `ASPNETCORE_ENVIRONMENT=Development` (the usual local setup) still sends nothing. If you relied on the old default elsewhere, set `FORGE_OPS_ENVIRONMENT=development` there. The enabled environments are unchanged: `Production` and `Staging`.
- With a DSN set and an environment that isn't enabled, setup now logs one warning, once per process: `AddForgeOpsTracker` when its `Configuration` is built, `ForgeOpsTrackerSdk.Configure`, or the first `ForgeOpsTrackerSdk.Capture` for an app that never calls `Configure`. It goes through `Configuration.Logger`, or stderr when there is none: `[ForgeOps] Not sending: this environment is "Development", and only production, staging are enabled. Set FORGE_OPS_ENVIRONMENT=production (or add "Development" to the enabled environments) to send from here.` Nothing is logged without a DSN.
- The README's ASP.NET Core snippets now read `config.Dsn = builder.Configuration["ForgeOps:Dsn"] ?? config.Dsn;`. The old plain assignment overwrote a DSN from `FORGE_OPS_DSN` with null whenever `ForgeOps:Dsn` wasn't in the app's configuration.

## 0.14.2 (2026-10-01)

- A console app that wraps its work in `ForgeOpsTrackerSdk.TraceAsync` and then exits straight away no longer loses the trace. Once a trace has finished, the static path now sends whatever traces are still queued when the process exits, together with any queued exceptions, waiting at most 5 seconds in all (or `ShutdownTimeout`, if that's shorter) rather than 5 seconds for each. A process that never traced or captured anything still doesn't wait. Apps using `AddForgeOpsTracker` already sent queued traces on shutdown and are unchanged.
- `ForgeOpsTrackerSdk.FlushAsync(cancellationToken)` now also waits for every trace finished so far, and the new `SpanQueue.FlushAsync(cancellationToken)` does the same for one queue.

## 0.14.1 (2026-10-01)

- A console app that captures an exception with `ForgeOpsTrackerSdk.Capture` and then exits straight away no longer loses it. Once something has been captured, the static path now sends whatever is still queued when the process exits, whether `Main` returns or the exception is rethrown unhandled, waiting at most 5 seconds (or `ShutdownTimeout`, if that's shorter). A process that never captured anything doesn't wait, and an unhandled exception is still never reported on its own. Apps using `AddForgeOpsTracker` already sent queued events on shutdown and are unchanged.
- New `ForgeOpsTrackerSdk.FlushAsync(cancellationToken)` and `DeliveryQueue.FlushAsync(cancellationToken)` wait until every exception captured so far has been sent, returning `true` once nothing is left.

## 0.14.0 (2026-09-29)

- SQL masking now matches the server's newer rules, so fewer values can slip through. A backslash-escaped quote no longer ends a string early (`'o\'brien'` and `E'o\'brien'` are each one `?`), a string's type prefix goes with it (`X'DEADBEEF'`, `N'...'`, `B'...'`, `U&'...'`), and hex, binary, exponent and leading-dot numbers are masked (`0x1F`, `0b101`, `3e10`, `1.5E-3`, `.5`). A database span whose `db.system` is `mysql` or `mariadb` also has its double-quoted strings masked, since those databases read `"..."` as a string, not a name; `SqlStatement.MaskStatement(statement, system)` is the new overload behind that. Digits and word characters from other scripts no longer change what counts as a number, matching the server. A string cut off right after a backslash is masked to the end too. Applies to database spans and to the statement sent with a database error.

## 0.13.0 (2026-09-25)

- A database span can now carry the SQL it ran. New `ForgeOpsTrackerSdk.DatabaseSpan`, `DatabaseSpanAsync`, `StartDatabaseSpan` and `RecordDatabaseSpan` take the statement and an optional `dbSystem`, and record a `database` span whose data holds the statement as `db.statement` and the database name, lowercased, as `db.system` (`DatabaseSpanData(sql, dbSystem)` builds that dictionary for `Span`/`RecordSpan`). The statement is masked (every string and number becomes `?`) and cut to 4000 characters when the span is recorded, and a `db.statement` on any database span is masked the same way, so the SQL as written never leaves the process. Parameter values are never read.

## 0.12.0 (2026-09-25)

- What changed: `ChangeTracker.RecordChange(kind, title, details, environment, service, actor, url, id, occurredAt)` records one change (a feature flag flip, a config edit, a migration) so it shows up next to the errors it might explain. `AddForgeOpsTracker` registers `ChangeTracker` (inject it); `ForgeOpsTrackerSdk.RecordChange` does the same without DI. `kind` is one of the `ChangeKind` constants (`feature_flag`, `config`, `migration`, `dependency`, `infrastructure`, `other`); anything else is sent as `other`. `environment` defaults to `Configuration.Environment` and `occurredAt` to now. Queued and sent from a background task, never throws, and is a no-op when the client isn't enabled.
- A change snapshot is sent once per process at startup (from a hosted service in DI, or from `ForgeOpsTrackerSdk.Configure`), off the startup path, so ForgeOps can record what changed between deploys on its own. It carries the .NET runtime version. There's no reliable runtime list of an app's package versions in .NET, so dependencies are left out rather than guessed. New `DetectChanges` (default true) turns the snapshot off.
- New opt-in `TrackEnvVarNames` (default false) adds environment variable names (never values) to the snapshot, leaving out host-specific ones (`HOSTNAME`, `PATH`, `LC_*`, `KUBERNETES_*`, `*_SERVICE_HOST`, this client's own `FORGE_OPS_*`, and similar) so a fleet doesn't look like it changes on every restart.

## 0.11.0

- Distributed tracing across services, using the W3C Trace Context standard (`traceparent`). Every request that passes through any of this client's middlewares now gets a trace id; one that arrives with a valid `traceparent` header continues that trace, and its root span points at the caller's span. Otherwise it reuses ASP.NET Core's own request `Activity` trace id when there is one, or starts a fresh one. `ForgeOpsTrackerSdk.CurrentTraceId` returns it.
- Every outbound `IHttpClientFactory` call made during a request (or inside `TraceAsync`) now sends a `traceparent` header whose parent id is that call's own `http` span. A `traceparent` you set yourself is never replaced, and this client's own requests to ForgeOps never carry one. New `PropagateTraces` (default true) and `TracePropagationTargets` (default null, meaning every host; or a list of host strings, each matching that host and its subdomains, and/or `Regex`es) control where the header goes.
- Every error reported during a request now says where it happened: `trace_id`, `transaction_name` (the same name performance samples and traces already use), and `endpoint` (the HTTP method plus the matched route template, e.g. `GET /orders/{id}`, never the raw path). This covers the middleware, `ForgeOpsTrackerSdk.Capture`, and an injected `ForgeOpsReporter`, and an error reported from inside an endpoint already has them. Left out outside a request (`trace_id` aside, inside a `TraceAsync` trace). Structured fields, so they're never PII-scrubbed.
- An errored request's trace is always sent, however fast it was: an exception escaping the pipeline, or any error reported while the request ran. Fast, successful requests are unchanged.
- With `TrackTracing = false`, requests still get a trace id (attached to errors and propagated in the header, since that is also what links errors across services); only span reporting stops.
- Trace and span ids were already W3C shaped (32 and 16 lowercase hex characters); they are now also guaranteed never to be all zeros, the one value the standard reserves as invalid.

## 0.10.0

- Database errors now say where to look. When an error carries the SQL behind a failed database call (a `Statement`, `Sql` or `CommandText` property, such as Npgsql's `PostgresException.Statement`, or `exception.Data["forge_ops_sql"]` for any exception type), the event carries the names of the stored procedure, table and view that SQL touched. On by default (`CaptureSqlObjects`); names are identifiers, never values. New opt-in `CaptureSqlStatement` (default false) also sends the statement itself, with every string and number replaced by `?`. Each project has its own server-side setting that can stop the statement being stored regardless of this flag; the names are still kept.

## 0.9.1

- Documentation only: the README and package description now describe ForgeOps as a hosted service, link to getforgeops.net, and show the real host in the connection string example instead of a placeholder. No code changes.

## 0.9.0

- Performance percentiles: every performance sample now carries a small latency histogram alongside
  its count/sum/max (fixed buckets of 50, 100, 250, 500, 1000, 2500, 5000 and 10000ms, plus an
  overflow bucket), so ForgeOps can show an approximate p50/p95/p99 per transaction instead of
  only an average. No new config; this rides the existing performance tracking flag and flush
  interval. Also fixes a flush race: a request recorded while a flush was still delivering could be discarded
  when that delivery succeeded; now only what was actually delivered is removed.

## 0.8.0

- Custom metrics and infrastructure monitoring: `MetricsReporter.CaptureMetric(name, value = 1)`
  records a named business event (a signup, a payment) and `CaptureInfrastructureMetric(name, value,
  hostname)` a CPU/memory/disk reading from one of your own hosts. Registered by `AddForgeOpsTracker`
  (inject it), and available without DI as `ForgeOpsTrackerSdk.CaptureMetric`/
  `CaptureInfrastructureMetric`/`FlushMetricsAsync`. Buffered, flushed as a batch every
  `MetricFlushInterval`/`InfrastructureMetricFlushInterval` and at shutdown. Nothing is automatic, so
  there is no `TrackMetrics` flag.

## 0.7.0

- Distributed tracing: `PerformanceTrackingMiddleware` starts a trace per request and sends it to
  the new `/spans` endpoint when the request took at least `Configuration.TraceCaptureThreshold`
  (1s); `Configuration.TrackTracing` (on) turns the feature off. Outbound `IHttpClientFactory`
  requests become `http` spans (`METHOD host`, never the path) via a handler `AddForgeOpsTracker`
  registers on every factory client. Add your own with `ForgeOpsTrackerSdk.Span`/`SpanAsync`/
  `StartSpan`/`RecordSpan`; `TraceAsync` traces non-web code. Parallel tasks each parent under their
  own span (an `AsyncLocal` current-span id).

## 0.6.0

- Breadcrumbs: a trail of the request/controller lifecycle leading up to an error, recorded
  automatically by `PerformanceTrackingMiddleware` (on by default, same as every other automatic
  instrumentation this client does; opt out with `Configuration.TrackBreadcrumbs = false`, or tune
  `MaxBreadcrumbs`, default 30). `ForgeOpsTrackerSdk.AddBreadcrumb(message, category, level, data)`
  adds your own, regardless of whether the automatic source is on, and works outside a request
  entirely too (a console app, a background worker with no `BreadcrumbContextMiddleware`
  installed). Unlike the affected user, a breadcrumb's message/data is scrubbed for likely PII.
  Scoped with a second `AsyncLocal<BreadcrumbBuffer?>` on `ForgeOpsTrackerSdk`, the same mechanism
  `CurrentUser` already uses for the affected user, so one request's trail never bleeds into
  another's on a reused thread-pool thread; a new `BreadcrumbContextMiddleware`
  (`app.UseForgeOpsTrackerBreadcrumbs()`) gives every request its own trail and must be registered
  first, before every other `ForgeOpsTracker*` middleware.
