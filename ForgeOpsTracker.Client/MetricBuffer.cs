using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// Collects individual CaptureMetric/CaptureInfrastructureMetric calls in-process and periodically
/// flushes them as one batch, rather than one network call per capture. Unlike PerformanceFlusher
/// this keeps a list of individually meaningful entries instead of summing them into buckets: a
/// customer's own signup or payment is exactly the kind of thing they will want a genuinely accurate
/// count/sum of later, so the server stores one row per entry as-is. Ported from
/// gems/forge_ops_tracker's metric_buffer.rb and infrastructure_metric_buffer.rb, which are the same
/// class twice; here it is one generic class instantiated twice.
///
/// Three deliberate differences from the Ruby buffers:
///
/// - A flush snapshots the first N entries and, on success, removes exactly those N, instead of
///   resetting the whole list, so an entry recorded while the request is in flight is kept for the
///   next flush rather than lost.
/// - The buffer is capped at MaxEntries, and once full further entries are dropped until a flush
///   empties it: an endpoint that is down fails every flush, and an uncapped buffer would then keep
///   growing until the batch is given up on. Dropping the newest rather than the oldest keeps the
///   entries a flush is delivering at the front of the list, which is what makes removing exactly
///   them afterward exact.
/// - A NaN or infinite value is dropped at record time: System.Text.Json refuses to write one, and
///   one bad entry would make every batch behind it fail to send.
///
/// The same Task.Run delay loop and IAsyncDisposable final flush PerformanceFlusher uses.
/// </summary>
public sealed class MetricBuffer<T> : IAsyncDisposable
{
    public const int MaxEntries = 1000;

    private readonly Configuration _configuration;
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task<DeliveryResult>> _deliver;
    private readonly Func<TimeSpan> _interval;
    private readonly object _lock = new();
    private readonly List<T> _entries = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private int _failedFlushes;

    /// <param name="deliver">Takes a batch and returns whether delivery succeeded; false is a failure worth retrying.</param>
    public MetricBuffer(Configuration configuration, Func<IReadOnlyList<T>, CancellationToken, Task<bool>> deliver, Func<TimeSpan> interval)
        : this(configuration, interval, async (entries, ct) => DeliveryResults.FromBool(await deliver(entries, ct).ConfigureAwait(false)))
    {
    }

    /// <summary>
    /// The same buffer, for a deliver that can also say the server rejected the batch. The arguments
    /// are in a different order only so a lambda can never be ambiguous between the two.
    /// </summary>
    internal MetricBuffer(Configuration configuration, Func<TimeSpan> interval, Func<IReadOnlyList<T>, CancellationToken, Task<DeliveryResult>> deliver)
    {
        _configuration = configuration;
        _deliver = deliver;
        _interval = interval;
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Adds one entry; returns whether it was kept.</summary>
    public bool Record(T entry, double value)
    {
        if (!double.IsFinite(value))
        {
            _configuration.Logger?.LogDebug("[ForgeOpsTracker] dropped a metric with a non-finite value");
            return false;
        }

        lock (_lock)
        {
            if (_entries.Count >= MaxEntries)
            {
                _configuration.Logger?.LogDebug("[ForgeOpsTracker] metric buffer full, dropping a metric");
                return false;
            }

            _entries.Add(entry);
            _worker ??= Task.Run(RunAsync);
            return true;
        }
    }

    /// <summary>
    /// Delivers everything buffered so far as one batch. A failed delivery keeps every entry, so the
    /// next flush's batch just grows, unless the server rejected the batch (it would reject it again)
    /// or this is the fifth flush in a row to fail (DeliveryResults.MaxFailedFlushes): then the batch
    /// is dropped, which is also what makes room in a full buffer again.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        List<T> snapshot;
        lock (_lock)
        {
            if (_entries.Count == 0) return;
            snapshot = new List<T>(_entries);
        }

        var result = await _deliver(snapshot, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            if (result == DeliveryResult.Failed && ++_failedFlushes < DeliveryResults.MaxFailedFlushes) return;
            _failedFlushes = 0;

            // Exactly the entries just delivered (or given up on): anything recorded while the
            // request was in flight sits after them and stays for the next flush.
            _entries.RemoveRange(0, Math.Min(snapshot.Count, _entries.Count));
        }

        if (result != DeliveryResult.Delivered)
        {
            _configuration.Logger?.LogDebug("[ForgeOpsTracker] dropped {Count} metrics: {Reason}", snapshot.Count, result.DropReason());
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_interval(), _cts.Token).ConfigureAwait(false);
                try
                {
                    await FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] metric flush error");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync below; the final flush there is the real last word.
        }
    }

    /// <summary>Stops the timer and flushes whatever is left, bounded by Configuration.ShutdownTimeout.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_worker != null) await Task.WhenAny(_worker, Task.Delay(_configuration.ShutdownTimeout)).ConfigureAwait(false);

        var finalFlush = FlushAsync();
        await Task.WhenAny(finalFlush, Task.Delay(_configuration.ShutdownTimeout)).ConfigureAwait(false);

        _cts.Dispose();
    }
}
