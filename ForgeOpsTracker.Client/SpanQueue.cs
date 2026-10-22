using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>Same shape as DeliveryQueue: a bounded channel drained by one background task, delivering finished traces to /spans.</summary>
public sealed class SpanQueue : IAsyncDisposable
{
    private readonly Configuration _configuration;
    private readonly IClient _client;
    private readonly Channel<TracePayload> _channel;
    private readonly Task _worker;

    // Traces accepted by Push and not yet delivered (or given up on), counted from Push the same way
    // DeliveryQueue counts events, so FlushAsync never sees zero while the worker holds one unsent.
    private int _pending;

    public SpanQueue(Configuration configuration, IClient client)
    {
        _configuration = configuration;
        _client = client;
        _channel = Channel.CreateBounded<TracePayload>(new BoundedChannelOptions(Math.Max(1, configuration.QueueSize))
        {
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Enqueues a trace, dropping it (never blocking the caller, which is a request that was already slow) when full.</summary>
    public bool Push(TracePayload trace)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(trace)) return true;

        Interlocked.Decrement(ref _pending);
        _configuration.Logger?.LogDebug("[ForgeOpsTracker] span queue full, dropping trace");
        return false;
    }

    /// <summary>
    /// Waits until every trace pushed so far has been delivered (or given up on), without stopping
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
    /// Stops taking new traces and waits up to <paramref name="timeout"/> for the worker to deliver
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
        await foreach (var trace in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _client.DeliverSpansAsync(trace).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] span worker error");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!await DrainAsync(_configuration.ShutdownTimeout).ConfigureAwait(false))
        {
            _configuration.Logger?.LogDebug("[ForgeOpsTracker] span worker did not drain within ShutdownTimeout, abandoning it");
        }
    }
}
