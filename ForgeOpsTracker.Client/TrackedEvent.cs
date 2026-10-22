using System.Text.Json.Serialization;

namespace ForgeOpsTracker;

/// <summary>
/// The JSON-able payload shape ForgeOps' ingestion API expects
/// (POST /api/v1/events): property names are snake_case via
/// JsonPropertyName to match the Rails side exactly, not this library's
/// otherwise-idiomatic PascalCase.
/// </summary>
public sealed record TrackedEvent
{
    [JsonPropertyName("exception_class")]
    public string ExceptionClass { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    // What strict privacy sends instead of the message (which is then ""): a one-way hash of it,
    // so errors still group. See StrictPrivacy.MessageFingerprint. Omitted entirely when null, as
    // it always is with strict privacy off.
    [JsonPropertyName("message_fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageFingerprint { get; init; }

    [JsonPropertyName("backtrace")]
    public List<BacktraceFrame> Backtrace { get; init; } = new();

    [JsonPropertyName("occurred_at")]
    public string OccurredAt { get; init; } = "";

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "";

    [JsonPropertyName("release")]
    public string? Release { get; init; }

    [JsonPropertyName("server_name")]
    public string? ServerName { get; init; }

    // Always sent (empty when there's nothing) unless strict privacy left nothing allowed in it,
    // in which case it's null and omitted.
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Context { get; init; } = new();

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Tags { get; init; } = new();

    [JsonPropertyName("sdk_name")]
    public string? SdkName { get; init; }

    // Omitted from the JSON entirely when null (WhenWritingNull), not serialized as "user": null,
    // to match every other client's own "no key at all when no user was given" wire shape.
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? User { get; init; }

    // Same "omitted entirely when there's nothing to send" treatment as User above, matching
    // gems/forge_ops_tracker/lib/forge_ops_tracker/event_builder.rb's own
    // "if breadcrumbs && !breadcrumbs.empty?" guard: null here (never an empty list) whenever
    // there's no trail, or trail recording never even ran for this request.
    [JsonPropertyName("breadcrumbs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BreadcrumbEntry>? Breadcrumbs { get; init; }

    // Procedure/function and table/view names the failing SQL touched, and (opt-in) the statement
    // itself with every string and number masked; see SqlStatement. Omitted entirely when null,
    // the same "no key at all" wire shape User and Breadcrumbs above already use.
    [JsonPropertyName("sql_objects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? SqlObjects { get; init; }

    [JsonPropertyName("sql_statement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SqlStatement { get; init; }

    // Where the error happened, set by ForgeOpsReporter during a web request (see RequestState):
    // transaction_name is the same name performance samples and the request's root span use,
    // endpoint the HTTP method plus the matched route pattern ("GET /orders/{id}", never the raw
    // path), trace_id the request's 32-character lowercase hex W3C trace id, which is also what links
    // this error to errors other services reported for the same trace. Omitted entirely when null,
    // as all three are outside a request (trace_id aside, inside a TraceAsync trace).
    [JsonPropertyName("transaction_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionName { get; init; }

    [JsonPropertyName("endpoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endpoint { get; init; }

    [JsonPropertyName("trace_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }
}

public sealed record BacktraceFrame
{
    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("in_app")]
    public bool InApp { get; init; }

    [JsonPropertyName("context_line")]
    public string? ContextLine { get; init; }

    [JsonPropertyName("pre_context")]
    public List<string>? PreContext { get; init; }

    [JsonPropertyName("post_context")]
    public List<string>? PostContext { get; init; }
}
