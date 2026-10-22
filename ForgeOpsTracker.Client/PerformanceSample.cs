using System.Text.Json.Serialization;

namespace ForgeOpsTracker;

/// <summary>
/// The JSON-able payload shape for one distinct transaction's aggregate report
/// (part of the POST /api/v1/performance_samples batch); property names are snake_case via
/// JsonPropertyName, the same way SessionCheckin's own shape already matches the Rails side.
/// </summary>
public sealed record PerformanceSample
{
    [JsonPropertyName("transaction_name")]
    public string TransactionName { get; init; } = "";

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "";

    [JsonPropertyName("release")]
    public string? Release { get; init; }

    [JsonPropertyName("period_started_at")]
    public string PeriodStartedAt { get; init; } = "";

    [JsonPropertyName("period_ended_at")]
    public string PeriodEndedAt { get; init; } = "";

    [JsonPropertyName("request_count")]
    public long RequestCount { get; init; }

    [JsonPropertyName("duration_sum_ms")]
    public double DurationSumMs { get; init; }

    [JsonPropertyName("max_duration_ms")]
    public double MaxDurationMs { get; init; }

    /// <summary>
    /// A count per fixed latency bucket ("50", "100", ... "10000", "inf"), see HistogramBucketer:
    /// what lets ForgeOps compute an approximate p50/p95/p99, not just an average.
    /// </summary>
    [JsonPropertyName("histogram")]
    public IReadOnlyDictionary<string, long> Histogram { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// The wire shape the ingestion API expects for a batch of samples: <c>{"samples": [...]}</c>,
/// not a bare array, matching every other SDK's own performance_samples payload.
/// </summary>
public sealed record PerformanceSamplesBatch
{
    [JsonPropertyName("samples")]
    public IReadOnlyList<PerformanceSample> Samples { get; init; } = Array.Empty<PerformanceSample>();
}
