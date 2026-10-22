using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// Counts requests and crashes in-process (see SessionTrackingMiddleware) and periodically
/// flushes the totals as one small aggregate report, rather than one network call per request.
/// Ported from gems/forge_ops_tracker/lib/forge_ops_tracker/session_flusher.rb.
///
/// A Task.Run delay-loop, matching DeliveryQueue's own async style, not its Channel&lt;T&gt;
/// consumer shape; that pattern exists for a push-triggered consumer, and this flusher's trigger
/// is a clock, not a per-event signal. IAsyncDisposable, registered as a DI singleton the same
/// way DeliveryQueue is: the host's normal singleton disposal on shutdown flushes whatever's
/// left automatically, no extra IHostedService wiring needed.
/// </summary>
public sealed class SessionFlusher : IAsyncDisposable
{
    private readonly Configuration _configuration;
    private readonly IClient _client;
    private readonly object _lock = new();
    private long _sessionsCount;
    private long _crashedSessionsCount;
    private DateTimeOffset _periodStartedAt = DateTimeOffset.UtcNow;
    private int _failedFlushes;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    internal Configuration Configuration => _configuration;

    public SessionFlusher(Configuration configuration, IClient client)
    {
        _configuration = configuration;
        _client = client;
        _worker = Task.Run(RunAsync);
    }

    public void RecordSession(bool crashed)
    {
        if (!_configuration.TrackSessions || !_configuration.IsEnabled()) return;

        lock (_lock)
        {
            _sessionsCount++;
            if (crashed) _crashedSessionsCount++;
        }
    }

    /// <summary>
    /// Snapshots and resets the in-process counters, then delivers them. A failed delivery
    /// keeps the counts where they are rather than resetting, so the next flush's window just
    /// grows instead of losing what was already tallied; there's no other copy of this data
    /// anywhere. Two limits on that: a checkin the server rejected would be rejected again however
    /// often it is re-sent, so its counts are dropped at once, and after
    /// DeliveryResults.MaxFailedFlushes failed flushes in a row the counts are dropped too and the
    /// window starts clean.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        SessionCheckin? snapshot;
        lock (_lock)
        {
            if (_sessionsCount == 0)
            {
                snapshot = null;
            }
            else
            {
                snapshot = new SessionCheckin
                {
                    Release = _configuration.Release,
                    Environment = _configuration.Environment,
                    PeriodStartedAt = _periodStartedAt.ToString("O"),
                    PeriodEndedAt = DateTimeOffset.UtcNow.ToString("O"),
                    SessionsCount = _sessionsCount,
                    CrashedSessionsCount = _crashedSessionsCount
                };
            }
        }

        if (snapshot is null) return;

        var result = await _client.SessionCheckinResultAsync(snapshot, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            if (result == DeliveryResult.Failed && ++_failedFlushes < DeliveryResults.MaxFailedFlushes) return;
            _failedFlushes = 0;

            _sessionsCount = 0;
            _crashedSessionsCount = 0;
            _periodStartedAt = DateTimeOffset.UtcNow;
        }

        if (result != DeliveryResult.Delivered)
        {
            _configuration.Logger?.LogDebug(
                "[ForgeOpsTracker] dropped a session checkin of {Count} sessions: {Reason}", snapshot.SessionsCount, result.DropReason());
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_configuration.SessionFlushInterval, _cts.Token).ConfigureAwait(false);
                try
                {
                    await FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Per-tick, not left to kill the loop: one bad flush must not stop every
                    // flush after it.
                    _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] session flush error");
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
    /// Configuration.ShutdownTimeout (see DeliveryQueue.DisposeAsync's own comment for why a
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
