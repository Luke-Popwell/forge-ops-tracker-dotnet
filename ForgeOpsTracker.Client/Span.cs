using System.Text.Json.Serialization;

namespace ForgeOpsTracker;

/// <summary>One span on the wire (part of the POST /api/v1/spans payload); snake_case names via JsonPropertyName.</summary>
public sealed record Span
{
    [JsonPropertyName("span_id")]
    public string SpanId { get; init; } = "";

    [JsonPropertyName("parent_span_id")]
    public string? ParentSpanId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "service";

    [JsonPropertyName("started_at")]
    public string StartedAt { get; init; } = "";

    [JsonPropertyName("duration_ms")]
    public double DurationMs { get; init; }

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "";

    [JsonPropertyName("release")]
    public string? Release { get; init; }

    [JsonPropertyName("data")]
    public IDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();
}

/// <summary>One finished trace: <c>{"trace_id": ..., "spans": [...]}</c>, the shape the ingestion API expects.</summary>
public sealed record TracePayload
{
    [JsonPropertyName("trace_id")]
    public string TraceId { get; init; } = "";

    [JsonPropertyName("spans")]
    public IReadOnlyList<Span> Spans { get; init; } = Array.Empty<Span>();
}
