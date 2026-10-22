namespace ForgeOpsTracker;

/// <summary>
/// Buckets a single duration into one of a fixed set of latency-range labels, the building block
/// PerformanceFlusher uses to accumulate an approximate distribution (not just count/sum/max)
/// alongside every transaction bucket it already tallies. The server merges these counts across
/// matching samples at read time and walks cumulative counts to approximate a percentile, accurate
/// to the bucket width: this SDK never stores the raw duration list a true percentile would need.
/// Ported from gems/forge_ops_tracker/lib/forge_ops_tracker/histogram_bucketer.rb.
///
/// BoundariesMs is duplicated on the server side, in app/services/histogram_percentile.rb. Change
/// one, change the other, or a released SDK version and the server it talks to would silently
/// disagree about what each bucket label means.
/// </summary>
internal static class HistogramBucketer
{
    public static readonly IReadOnlyList<long> BoundariesMs = new long[] { 50, 100, 250, 500, 1000, 2500, 5000, 10_000 };

    /// <summary>
    /// Returns the label (a string) of the smallest boundary durationMs fits under, or "inf" for
    /// anything larger than the largest boundary. A string, not a number: this travels as a JSON
    /// object key once flushed, and JSON object keys are always strings.
    /// </summary>
    public static string BucketFor(double durationMs)
    {
        foreach (var boundary in BoundariesMs)
        {
            if (durationMs <= boundary) return boundary.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return "inf";
    }
}
