using System.Text.Json.Serialization;

namespace ForgeOpsTracker;

/// <summary>
/// The kinds ForgeOps accepts for a recorded change. The server rejects anything else, so
/// ChangeTracker.RecordChange maps an unknown kind to <see cref="Other"/> rather than sending it.
/// </summary>
public static class ChangeKind
{
    public const string FeatureFlag = "feature_flag";
    public const string Config = "config";
    public const string Migration = "migration";
    public const string Dependency = "dependency";
    public const string Infrastructure = "infrastructure";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = new[] { FeatureFlag, Config, Migration, Dependency, Infrastructure, Other };
}

/// <summary>
/// One explicit change (POST /api/v1/changes), snake_case via JsonPropertyName like every other
/// payload here. Optional fields are omitted entirely when null, never sent as JSON null.
/// </summary>
public sealed record ChangeRecord
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ChangeKind.Other;

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Details { get; init; }

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "";

    [JsonPropertyName("service")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Service { get; init; }

    [JsonPropertyName("actor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    [JsonPropertyName("occurred_at")]
    public string OccurredAt { get; init; } = "";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
}

/// <summary>
/// What this process sees at startup (POST /api/v1/change_snapshots); the server diffs it against
/// the previous snapshot for the same project, environment and service.
/// </summary>
public sealed record ChangeSnapshot
{
    [JsonPropertyName("environment")]
    public string Environment { get; init; } = "";

    [JsonPropertyName("service")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Service { get; init; }

    [JsonPropertyName("state")]
    public ChangeSnapshotState State { get; init; } = new();
}

/// <summary>
/// Only what .NET can report reliably: the runtime, plus environment variable names when
/// Configuration.TrackEnvVarNames opts in. There's no dependable list of the app's package
/// versions at runtime, so dependencies are left out (the server reads a missing key as unknown,
/// never as "everything removed"), and so is schema_version.
/// </summary>
public sealed record ChangeSnapshotState
{
    [JsonPropertyName("runtime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runtime { get; init; }

    [JsonPropertyName("env_var_names")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EnvVarNames { get; init; }
}
