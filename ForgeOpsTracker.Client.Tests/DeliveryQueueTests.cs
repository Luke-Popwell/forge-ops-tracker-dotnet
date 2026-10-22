using System.Threading.Channels;
using Moq;

namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/delivery_queue_spec.rb.
public class DeliveryQueueTests
{
    [Fact]
    public async Task DeliversAPushedPayloadViaTheClientOnItsBackgroundWorker()
    {
        var delivered = Channel.CreateUnbounded<TrackedEvent>();
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverAsync(It.IsAny<TrackedEvent>(), It.IsAny<CancellationToken>()))
            .Returns<TrackedEvent, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });

        await using var queue = new DeliveryQueue(new Configuration { QueueSize = 1 }, client.Object);
        queue.Push(new TrackedEvent { ExceptionClass = "RuntimeError" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await delivered.Reader.ReadAsync(cts.Token);

        Assert.Equal("RuntimeError", result.ExceptionClass);
    }

    [Fact]
    public async Task DropsAPayloadWithoutBlockingWhenTheQueueIsAlreadyFull()
    {
        // A client whose delivery never completes, so nothing ever drains
        // the queue and capacity stays exactly at QueueSize (1) for a
        // deterministic full-queue test. ShutdownTimeout is set short so
        // this test's own DisposeAsync (at the end of the `await using`
        // block) doesn't wait the full 10s default for a worker that, by
        // construction, never finishes.
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverAsync(It.IsAny<TrackedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<bool>().Task);

        await using var queue = new DeliveryQueue(
            new Configuration { QueueSize = 1, ShutdownTimeout = TimeSpan.FromMilliseconds(200) },
            client.Object);

        Assert.True(queue.Push(new TrackedEvent { ExceptionClass = "First" }));

        // The first push may already have been picked up by the worker
        // (draining the channel before the queue itself observes
        // "full"), so retry briefly rather than asserting on a single
        // racy attempt.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        var secondPushResult = true;
        while (DateTime.UtcNow < deadline)
        {
            secondPushResult = queue.Push(new TrackedEvent { ExceptionClass = "Second" });
            if (!secondPushResult) break;
        }

        Assert.False(secondPushResult);
    }

    [Fact]
    public async Task RecoversFromTheClientThrowingInsteadOfReturning()
    {
        var delivered = Channel.CreateUnbounded<TrackedEvent>();
        var callCount = 0;
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverAsync(It.IsAny<TrackedEvent>(), It.IsAny<CancellationToken>()))
            .Returns<TrackedEvent, CancellationToken>((payload, _) =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("boom");

                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });

        // A larger queue than the other tests, so both pushes below can
        // be enqueued regardless of exactly when the worker gets
        // scheduled.
        await using var queue = new DeliveryQueue(new Configuration { QueueSize = 10 }, client.Object);

        queue.Push(new TrackedEvent { ExceptionClass = "First" }); // throws inside the worker loop; must not kill it
        queue.Push(new TrackedEvent { ExceptionClass = "Second" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await delivered.Reader.ReadAsync(cts.Token);

        Assert.Equal("Second", result.ExceptionClass);
    }
}
