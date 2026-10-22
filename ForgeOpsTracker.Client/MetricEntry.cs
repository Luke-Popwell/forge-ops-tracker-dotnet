using System.Text.Json.Serialization;

namespace ForgeOpsTracker;

/// <summary>One CaptureMetric call, as the ingestion API expects it (snake_case via JsonPropertyName).</summary>
public sealed record CustomMetricEntry
{
    [JsonPropertyName("metric_name")]
    public string MetricName { get; init; } = "";

    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "";

    [JsonPropertyName("release")]
    public string? Release { get; init; }

    [JsonPropertyName("recorded_at")]
    public string RecordedAt { get; init; } = "";
}

/// <summary>One CaptureInfrastructureMetric call.</summary>
public sealed record InfrastructureMetricEntry
{
    [JsonPropertyName("metric_name")]
    public string MetricName { get; init; } = "";

    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = "";

    [JsonPropertyName("recorded_at")]
    public string RecordedAt { get; init; } = "";
}

/// <summary>The wire shape for a batch of custom metrics: <c>{"metrics": [...]}</c>.</summary>
public sealed record CustomMetricsBatch
{
    [JsonPropertyName("metrics")]
    public IReadOnlyList<CustomMetricEntry> Metrics { get; init; } = Array.Empty<CustomMetricEntry>();
}

/// <summary>The wire shape for a batch of infrastructure readings: <c>{"metrics": [...]}</c>.</summary>
public sealed record InfrastructureMetricsBatch
{
    [JsonPropertyName("metrics")]
    public IReadOnlyList<InfrastructureMetricEntry> Metrics { get; init; } = Array.Empty<InfrastructureMetricEntry>();
}
