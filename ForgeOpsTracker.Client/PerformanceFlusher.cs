using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// Accumulates one distinct transaction's own requests within the current flush window.
/// </summary>
internal sealed class PerformanceBucket
{
    public long Count;
    public double DurationSumMs;
    public double MaxDurationMs;
    public Dictionary<string, long> Histogram = new();
}

/// <summary>
/// Times requests in-process, bucketed by transaction name (see PerformanceTrackingMiddleware),
/// and periodically flushes each distinct bucket as one small aggregate report, rather than one
/// network call per request. A direct structural port of SessionFlusher, bucketed by transaction
/// name in a Dictionary instead of two scalar counters.
///
/// Same Task.Run delay-loop as SessionFlusher, for the same reason: the trigger is a clock, not a
/// per-event signal. IAsyncDisposable, registered as a DI singleton the same way SessionFlusher
/// is: the host's normal singleton disposal on shutdown flushes whatever's left automatically, no
/// extra IHostedService wiring needed.
/// </summary>
public sealed class PerformanceFlusher : IAsyncDisposable
{
    private readonly Configuration _configuration;
    private readonly IClient _client;
    private readonly object _lock = new();
    private Dictionary<string, PerformanceBucket> _buckets = new();
    private DateTimeOffset _periodStartedAt = DateTimeOffset.UtcNow;
    private int _failedFlushes;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public PerformanceFlusher(Configuration configuration, IClient client)
    {
        _configuration = configuration;
        _client = client;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Buckets one request's own duration under transactionName.
    /// </summary>
    public void Record(string transactionName, double durationMs)
    {
        if (!_configuration.TrackPerformance || !_configuration.IsEnabled()) return;

        // Under strict privacy a raw path (the fallback when no route matched) loses its ids.
        var name = _configuration.StrictPrivacy ? StrictPrivacy.SpanName(transactionName) : transactionName;
        lock (_lock)
        {
            if (!_buckets.TryGetValue(name, out var bucket))
            {
                bucket = new PerformanceBucket();
                _buckets[name] = bucket;
            }

            bucket.Count++;
            bucket.DurationSumMs += durationMs;
            if (durationMs > bucket.MaxDurationMs) bucket.MaxDurationMs = durationMs;
            // The distribution count/sum/max can't reconstruct: see HistogramBucketer for why the
            // server approximates a percentile from these bucket counts.
            var label = HistogramBucketer.BucketFor(durationMs);
            bucket.Histogram[label] = bucket.Histogram.GetValueOrDefault(label) + 1;
        }
    }

    /// <summary>
    /// Snapshots the buffered buckets, then delivers them as one batch. A failed delivery keeps
    /// every bucket where it is rather than resetting, so the next flush's batch just grows
    /// instead of losing what was already tallied; there's no other copy of this data anywhere.
    /// Two limits on that: a batch the server rejected would be rejected again however often it is
    /// re-sent, so it is dropped at once, and so is a batch whose delivery has failed
    /// DeliveryResults.MaxFailedFlushes flushes in a row.
    ///
    /// Only exactly what this snapshot delivered (or gave up on) is removed afterward, subtracted from whatever is
    /// in each bucket by then, never the whole dictionary reset: Record can run on another thread
    /// while delivery is in flight (the lock is released around the network call), so a record for
    /// a transaction already in the snapshot, or a brand-new one, can land between the snapshot
    /// and delivery succeeding, and resetting afterward would silently discard it. MaxDurationMs
    /// is left as whatever is currently on the bucket, sent or not: a max can't be "subtracted"
    /// back out, and leaving it never overstates the next period's own max.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        List<PerformanceSample>? samples;
        Dictionary<string, PerformanceBucket> sent;
        DateTimeOffset periodEndedAt;
        lock (_lock)
        {
            if (_buckets.Count == 0) return;

            periodEndedAt = DateTimeOffset.UtcNow;
            // Copy each bucket's own values out while still holding the lock, rather than handing
            // the live dictionary itself to code that runs after the lock is released: Record
            // keeps mutating _buckets concurrently once we unlock, and enumerating a Dictionary
            // while another thread mutates it is undefined behavior in .NET, not just a data race.
            samples = _buckets.Select(entry => new PerformanceSample
            {
                TransactionName = entry.Key,
                Environment = _configuration.Environment,
                Release = _configuration.Release,
                PeriodStartedAt = _periodStartedAt.ToString("O"),
                PeriodEndedAt = periodEndedAt.ToString("O"),
                RequestCount = entry.Value.Count,
                DurationSumMs = entry.Value.DurationSumMs,
                MaxDurationMs = entry.Value.MaxDurationMs,
                Histogram = new Dictionary<string, long>(entry.Value.Histogram)
            }).ToList();
            sent = _buckets.ToDictionary(
                entry => entry.Key,
                entry => new PerformanceBucket
                {
                    Count = entry.Value.Count,
                    DurationSumMs = entry.Value.DurationSumMs,
                    Histogram = new Dictionary<string, long>(entry.Value.Histogram)
                });
        }

        var result = await _client.PerformanceSamplesResultAsync(
            new PerformanceSamplesBatch { Samples = samples }, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            if (result == DeliveryResult.Failed && ++_failedFlushes < DeliveryResults.MaxFailedFlushes) return;
            _failedFlushes = 0;

            foreach (var (transactionName, deliveredBucket) in sent)
            {
                if (!_buckets.TryGetValue(transactionName, out var current)) continue;

                current.Count -= deliveredBucket.Count;
                current.DurationSumMs = Math.Max(current.DurationSumMs - deliveredBucket.DurationSumMs, 0.0);
                foreach (var (label, count) in deliveredBucket.Histogram)
                {
                    var remaining = current.Histogram.GetValueOrDefault(label) - count;
                    if (remaining > 0) current.Histogram[label] = remaining;
                    else current.Histogram.Remove(label);
                }

                if (current.Count <= 0) _buckets.Remove(transactionName);
            }

            _periodStartedAt = periodEndedAt;
        }

        if (result != DeliveryResult.Delivered)
        {
            _configuration.Logger?.LogDebug(
                "[ForgeOpsTracker] dropped performance samples for {Count} transactions: {Reason}", sent.Count, result.DropReason());
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_configuration.PerformanceFlushInterval, _cts.Token).ConfigureAwait(false);
                try
                {
                    await FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Per-tick, not left to kill the loop: one bad flush must not stop every
                    // flush after it.
                    _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] performance flush error");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync below; the final flush there is the real last word.
        }
    }

    /// <summary>
    /// Stops the periodic timer and flushes whatever's left, bounded by
    /// Configuration.ShutdownTimeout (see SessionFlusher.DisposeAsync's own comment for why a
    /// bounded wait, not an unbounded one). Runs automatically on app shutdown when registered
    /// via AddForgeOpsTracker.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Task.WhenAny(_worker, Task.Delay(_configuration.ShutdownTimeout)).ConfigureAwait(false);

        var finalFlush = FlushAsync();
        await Task.WhenAny(finalFlush, Task.Delay(_configuration.ShutdownTimeout)).ConfigureAwait(false);

        _cts.Dispose();
    }
}
