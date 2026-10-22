using System.Globalization;
using System.Text.Json.Serialization;

namespace ForgeOpsTracker;

/// <summary>
/// The JSON-able payload shape for one breadcrumb entry: property names are snake_case via
/// JsonPropertyName, matching TrackedEvent's own convention. Ported from
/// gems/forge_ops_tracker/lib/forge_ops_tracker/breadcrumb_buffer.rb's own entry hash shape
/// (category/message/level/timestamp/data).
/// </summary>
public sealed record BreadcrumbEntry
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("level")]
    public string Level { get; init; } = "info";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = "";

    [JsonPropertyName("data")]
    public Dictionary<string, object?> Data { get; init; } = new();
}

/// <summary>
/// A bounded, in-order trail of whatever happened recently in this request: the ASP.NET Core
/// request/controller lifecycle (see PerformanceTrackingMiddleware's own automatic breadcrumb),
/// plus anything added by hand via ForgeOpsTrackerSdk.AddBreadcrumb. Ported from
/// gems/forge_ops_tracker/lib/forge_ops_tracker/breadcrumb_buffer.rb: the same ring buffer
/// contract (capped at Configuration.MaxBreadcrumbs, oldest entry dropped once full), but scoped
/// per request via a second AsyncLocal&lt;BreadcrumbBuffer?&gt; on ForgeOpsTrackerSdk rather than
/// Ruby's Thread.current, the identical mechanism ForgeOpsTrackerSdk.CurrentUser already uses for
/// the affected user and for the same reason: ASP.NET Core request handling hops across
/// thread-pool threads at every await, which a plain field or a ThreadLocal would not survive.
///
/// Locked internally (unlike the Ruby/Node originals, which need no lock: Ruby's Thread.current
/// buffer is only ever touched by the one thread that owns it, and Node is single-threaded): a
/// single request's AsyncLocal value flows by reference to every child Task it spawns (e.g. a
/// Task.WhenAll of several downstream calls), so two genuinely concurrent branches of the same
/// request could call Add on the very same buffer instance from different thread-pool threads at
/// once. A plain List would be undefined behavior under that race; the lock keeps eviction and
/// snapshotting correct without asking every caller to know that detail.
/// </summary>
public sealed class BreadcrumbBuffer
{
    private readonly Configuration _configuration;
    private readonly object _lock = new();
    private readonly List<BreadcrumbEntry> _entries = new();

    public BreadcrumbBuffer(Configuration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Appends one entry, evicting the oldest once <see cref="Configuration.MaxBreadcrumbs"/> is
    /// exceeded. Re-reads MaxBreadcrumbs fresh on every call, not just once at construction: the
    /// same reasoning DeliveryQueue re-reads Configuration.QueueSize on every push, so a config
    /// change takes effect on whatever's added next, not just on a buffer created afterward.
    /// </summary>
    public void Add(string message, string category = "custom", string level = "info", IDictionary<string, object?>? data = null)
    {
        // Strict privacy never sends breadcrumbs, so none are kept either: a message is free text.
        var maxSize = Math.Max(_configuration.MaxBreadcrumbs, 0);
        if (maxSize == 0 || _configuration.StrictPrivacy) return;

        lock (_lock)
        {
            _entries.Add(new BreadcrumbEntry
            {
                Category = category,
                Message = message,
                Level = level,
                Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Data = data is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(data)
            });

            while (_entries.Count > maxSize)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    /// <summary>A copy, never the internal list itself.</summary>
    public IReadOnlyList<BreadcrumbEntry> All()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }
}
