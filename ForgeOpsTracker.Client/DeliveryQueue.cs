using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// A single injectable seam so ForgeOpsReporter is testable without a real
/// background worker running: mirrors how the Ruby gem's ErrorSubscriber
/// spec passes an instance_double(ForgeOpsTracker::DeliveryQueue, push: true)
/// rather than a real one.
/// </summary>
public interface IDeliveryQueue
{
    bool Push(TrackedEvent payload);
}

/// <summary>
/// A small in-process background task + bounded channel, so delivery
/// never blocks the caller that raised the error and never depends on the
/// host app having any particular job backend configured. Ported from
/// gems/forge_ops_tracker/lib/forge_ops_tracker/delivery_queue.rb, which
/// starts its worker thread lazily on first push specifically so it's
/// re-created fresh in each forked Puma worker process rather than being
/// carried across a fork (dead in the child otherwise). Kestrel/.NET
/// hosts don't fork worker processes that way, so this starts its
/// consumer eagerly at construction instead: there's no equivalent
/// hazard here to work around.
/// </summary>
public sealed class DeliveryQueue : IAsyncDisposable, IDeliveryQueue
{
    /// <summary>
    /// The most the static <see cref="ForgeOpsTrackerSdk"/> path waits at process exit for queued
    /// events and traces to go out, all of them together rather than each queue in turn (or
    /// <see cref="Configuration.ShutdownTimeout"/>, if that is shorter): long
    /// enough for one delivery at the default Timeout, short enough that an unreachable ForgeOps
    /// never holds up a console app or a cron job for long. Same bound as the Ruby gem's drain.
    /// </summary>
    public static readonly TimeSpan ExitDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Configuration _configuration;
    private readonly IClient _client;
    private readonly Channel<TrackedEvent> _channel;
    private readonly Task _worker;

    // Events accepted by Push and not yet delivered (or given up on), whether still in the channel
    // or in flight: counted from Push rather than from the worker, so FlushAsync never sees zero
    // while the worker holds an event it hasn't sent yet.
    private int _pending;

    public DeliveryQueue(Configuration configuration, IClient client)
    {
        _configuration = configuration;
        _client = client;
        _channel = Channel.CreateBounded<TrackedEvent>(new BoundedChannelOptions(Math.Max(1, configuration.QueueSize))
        {
            SingleReader = true,
            SingleWriter = false
        });

        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Enqueues a payload, dropping it silently (never blocking the
    /// caller) if the queue is already full: a burst of exceptions must
    /// never apply backpressure to the host app. TryWrite is the direct
    /// analog of the Ruby original's `queue.push(payload, true)`
    /// (non-blocking push that fails outright rather than waiting).
    /// </summary>
    public bool Push(TrackedEvent payload)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(payload)) return true;

        Interlocked.Decrement(ref _pending);
        _configuration.Logger?.LogDebug("[ForgeOpsTracker] delivery queue full, dropping event");
        return false;
    }

    /// <summary>
    /// Waits until every event pushed so far has been delivered (or given up on), without stopping
    /// the queue. Returns true once nothing is left, or false if
    /// <paramref name="cancellationToken"/> fires first. Never throws.
    /// </summary>
    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _pending) > 0)
        {
            try
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return Volatile.Read(ref _pending) == 0;
    }

    /// <summary>
    /// Stops taking new events and waits up to <paramref name="timeout"/> for the worker to deliver
    /// whatever is still queued, including one already in flight. Returns whether it finished in
    /// time. What <see cref="DisposeAsync"/> and the static path's ProcessExit hook both run.
    /// </summary>
    internal async Task<bool> DrainAsync(TimeSpan timeout)
    {
        _channel.Writer.TryComplete();

        var completed = await Task.WhenAny(_worker, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == _worker;
    }

    private async Task RunAsync()
    {
        await foreach (var payload in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _client.DeliverAsync(payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Per-item, not wrapping the whole loop: one bad
                // delivery must not kill the consumer for every event
                // after it, matching the Ruby original's per-iteration
                // rescue inside its loop.
                _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] delivery worker error");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    /// <summary>
    /// Signals no more writes and waits for the worker to drain whatever
    /// was already queued, rather than abandoning it. Runs automatically
    /// on app shutdown when registered via AddForgeOpsTracker (DI disposes
    /// every singleton, including this one, when the host stops). Bounded
    /// by Configuration.ShutdownTimeout rather than an unbounded await:
    /// see the property's own comment for why.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!await DrainAsync(_configuration.ShutdownTimeout).ConfigureAwait(false))
        {
            _configuration.Logger?.LogDebug("[ForgeOpsTracker] delivery worker did not drain within ShutdownTimeout, abandoning it");
        }
    }
}
