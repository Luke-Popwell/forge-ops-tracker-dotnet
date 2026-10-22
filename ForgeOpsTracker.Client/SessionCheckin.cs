using System.Text.Json.Serialization;

namespace ForgeOpsTracker;

/// <summary>
/// The JSON-able payload shape ForgeOps' ingestion API expects for one aggregate report
/// (POST /api/v1/session_checkins); property names are snake_case via JsonPropertyName to
/// match the Rails side exactly, the same TrackedEvent's own shape already does.
/// </summary>
public sealed record SessionCheckin
{
    [JsonPropertyName("release")]
    public string? Release { get; init; }

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "";

    [JsonPropertyName("period_started_at")]
    public string PeriodStartedAt { get; init; } = "";

    [JsonPropertyName("period_ended_at")]
    public string PeriodEndedAt { get; init; } = "";

    [JsonPropertyName("sessions_count")]
    public long SessionsCount { get; init; }

    [JsonPropertyName("crashed_sessions_count")]
    public long CrashedSessionsCount { get; init; }
}
