using System.Globalization;

namespace ForgeOpsTracker;

/// <summary>
/// One trace's worth of spans, sharing a single trace id. Held in an AsyncLocal on
/// ForgeOpsTrackerSdk, so it follows a request across awaits; because a request can fan out into
/// parallel tasks that all record into it, every mutation is under a lock (unlike BreadcrumbBuffer,
/// which a single flow owns). Which span is the parent of a new one is tracked separately, in a
/// second AsyncLocal holding the current span id, so parallel branches each see their own parent.
///
/// The trace id and the root span's remote parent come from the request's RequestState when there is
/// one: the caller's own ids when the request arrived with a usable traceparent header (see
/// TraceParent), so the root span nests under the caller's outgoing span even though the server
/// receives the two in different uploads.
/// </summary>
public sealed class SpanBuffer
{
    internal const int MaxSpans = 500;
    internal const string DbStatement = "db.statement";
    internal const string DbSystem = "db.system";

    // The kinds the ingestion API accepts; anything else would fail validation for the whole trace.
    private static readonly HashSet<string> Kinds = new() { "controller", "service", "database", "redis", "http", "job", "other" };

    private readonly Configuration _configuration;
    private readonly object _gate = new();
    private readonly List<Span> _spans = new();

    public SpanBuffer(Configuration configuration) : this(configuration, TraceParent.GenerateTraceId(), null)
    {
    }

    /// <param name="remoteParentSpanId">The calling service's span this trace's root runs under, or null when this trace started here.</param>
    public SpanBuffer(Configuration configuration, string traceId, string? remoteParentSpanId)
    {
        _configuration = configuration;
        TraceId = traceId;
        RemoteParentSpanId = remoteParentSpanId;
        RootSpanId = TraceParent.GenerateSpanId();
    }

    public string TraceId { get; }

    public string RootSpanId { get; }

    public string? RemoteParentSpanId { get; }

    internal static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public void Record(string spanId, string? parentSpanId, string name, string kind, DateTimeOffset startedAt, double durationMs, IDictionary<string, object?>? data)
    {
        lock (_gate)
        {
            if (_spans.Count >= MaxSpans - 1) return; // leave room for the root
            _spans.Add(Build(spanId, parentSpanId, name, kind, startedAt, durationMs, data));
        }
    }

    // Strict privacy: an outbound call is named by its method and host only, never a path that could
    // name a person, and data keeps only the masked statement and the database system.
    private Span Build(string spanId, string? parentSpanId, string name, string kind, DateTimeOffset startedAt, double durationMs, IDictionary<string, object?>? data) => new()
    {
        SpanId = spanId,
        ParentSpanId = parentSpanId,
        Name = _configuration.StrictPrivacy ? StrictPrivacy.SpanName(name) : name,
        Kind = Kinds.Contains(kind) ? kind : "other",
        StartedAt = Timestamp(startedAt),
        DurationMs = Math.Round(durationMs, 2),
        Environment = _configuration.Environment,
        Release = _configuration.Release,
        Data = SentData(kind, data)
    };

    private IDictionary<string, object?> SentData(string kind, IDictionary<string, object?>? data)
    {
        var sent = data is null ? new Dictionary<string, object?>() : kind == "database" ? MaskDatabaseData(data) : data;
        return _configuration.StrictPrivacy ? StrictPrivacy.SpanData(sent) : sent;
    }

    /// <summary>
    /// A copy of a database span's data with <c>db.statement</c> masked by <see cref="SqlStatement"/>
    /// (for the span's <c>db.system</c>, if any), so the SQL as written never reaches the payload
    /// however the dictionary was built. A statement
    /// that isn't a string, or is blank, is dropped. The caller's dictionary is never modified.
    /// </summary>
    internal static IDictionary<string, object?> MaskDatabaseData(IDictionary<string, object?> data)
    {
        if (!data.TryGetValue(DbStatement, out var raw)) return data;

        var masked = new Dictionary<string, object?>(data);
        masked.Remove(DbStatement);
        data.TryGetValue(DbSystem, out var system);
        if (SqlStatement.MaskStatement(raw as string, system as string) is { } value) masked[DbStatement] = value;
        return masked;
    }

    /// <summary>
    /// The wire payload once the trace is over: the root span plus everything recorded beneath it,
    /// or null when the root was faster than Configuration.TraceCaptureThreshold.
    /// </summary>
    public TracePayload? Finish(string rootName, DateTimeOffset startedAt, double durationMs) =>
        Finish(rootName, startedAt, durationMs, always: false);

    /// <summary>
    /// Same, but <paramref name="always"/> sends it however fast it was: an errored request's trace is
    /// what an issue page wants to show next to the error, slow or not.
    /// </summary>
    public TracePayload? Finish(string rootName, DateTimeOffset startedAt, double durationMs, bool always)
    {
        if (!always && durationMs < _configuration.TraceCaptureThreshold.TotalMilliseconds) return null;

        var all = new List<Span> { Build(RootSpanId, RemoteParentSpanId, rootName, "controller", startedAt, durationMs, null) };
        lock (_gate)
        {
            all.AddRange(_spans);
        }
        return new TracePayload { TraceId = TraceId, Spans = all };
    }
}
