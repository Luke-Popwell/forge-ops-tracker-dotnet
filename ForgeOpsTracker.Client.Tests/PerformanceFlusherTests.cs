using System.Net;
using System.Threading.Channels;
using Moq;

namespace ForgeOpsTracker.Tests;

// Mirrors SessionFlusherTests, bucketed by transaction name instead of two flat counters.
public class PerformanceFlusherTests
{
    private static Configuration NewConfiguration() => new()
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        EnabledEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Production" },
        Environment = "Production",
        Release = "1.2.3",
        // Long enough that no test relying on FlushAsync's own explicit call also races a real
        // background tick; the interval-driven test below overrides this down to something short
        // instead.
        PerformanceFlushInterval = TimeSpan.FromMinutes(10)
    };

    [Fact]
    public async Task BucketsByTransactionNameAndDeliversOnFlush()
    {
        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .Returns<PerformanceSamplesBatch, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });

        await using var flusher = new PerformanceFlusher(NewConfiguration(), client.Object);
        flusher.Record("GET /posts/{id}", 100);
        flusher.Record("GET /posts/{id}", 200);
        flusher.Record("GET /posts", 50);
        await flusher.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var payload = await delivered.Reader.ReadAsync(cts.Token);
        Assert.Equal(2, payload.Samples.Count);

        var show = Assert.Single(payload.Samples, s => s.TransactionName == "GET /posts/{id}");
        var index = Assert.Single(payload.Samples, s => s.TransactionName == "GET /posts");
        Assert.Equal(2, show.RequestCount);
        Assert.Equal(300, show.DurationSumMs);
        Assert.Equal(200, show.MaxDurationMs);
        Assert.Equal(1, index.RequestCount);
        Assert.Equal("1.2.3", show.Release);
        Assert.Equal("Production", show.Environment);
    }

    [Fact]
    public async Task StrictPrivacyTakesTheIdsOutOfARawPathTransactionNameAndKeepsARoute()
    {
        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .Returns<PerformanceSamplesBatch, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });
        var configuration = NewConfiguration();
        configuration.StrictPrivacy = true;

        await using var flusher = new PerformanceFlusher(configuration, client.Object);
        flusher.Record("GET /patients/42", 100);
        flusher.Record("GET /patients/43", 200);
        flusher.Record("GET /posts/{id}", 50);
        await flusher.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var payload = await delivered.Reader.ReadAsync(cts.Token);
        Assert.Equal(new[] { "GET /patients/:path", "GET /posts/{id}" }, payload.Samples.Select(s => s.TransactionName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(2, Assert.Single(payload.Samples, s => s.TransactionName == "GET /patients/:path").RequestCount);
    }

    [Fact]
    public async Task DoesNothingOnAFlushWithNothingRecorded()
    {
        var client = new Mock<IClient>();

        await using var flusher = new PerformanceFlusher(NewConfiguration(), client.Object);
        await flusher.FlushAsync();

        client.Verify(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KeepsTheBucketForTheNextFlushWhenDeliveryFails()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await using var flusher = new PerformanceFlusher(NewConfiguration(), client.Object);
        flusher.Record("GET /posts/{id}", 100);
        await flusher.FlushAsync(); // fails; the bucket must not be reset

        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .Returns<PerformanceSamplesBatch, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });
        flusher.Record("GET /posts/{id}", 100);
        await flusher.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await delivered.Reader.ReadAsync(cts.Token);
        var sample = Assert.Single(result.Samples);
        Assert.Equal(2, sample.RequestCount);
    }

    [Fact]
    public async Task DropsABatchTheServerRejectsAndNeverSendsItAgain()
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        var logger = new RecordingLogger();
        var configuration = NewConfiguration();
        configuration.Logger = logger;

        await using var flusher = new PerformanceFlusher(configuration, new Client(configuration, new HttpClient(handler)));
        flusher.Record("first", 100);
        await flusher.FlushAsync(); // rejected: the bucket is let go
        await flusher.FlushAsync(); // nothing left to send

        Assert.Single(handler.Bodies);
        Assert.Single(logger.Messages, m => m.Contains("dropped performance samples for 1 transactions: the server rejected it"));

        handler.Status = HttpStatusCode.Accepted;
        flusher.Record("second", 50);
        await flusher.FlushAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"transaction_name\":\"second\"", handler.Bodies[1]);
        Assert.DoesNotContain("first", handler.Bodies[1]);
    }

    [Fact]
    public async Task KeepsABatchTheServerAnswersWithA5xx()
    {
        var handler = new StatusHandler(HttpStatusCode.ServiceUnavailable);
        var configuration = NewConfiguration();

        await using var flusher = new PerformanceFlusher(configuration, new Client(configuration, new HttpClient(handler)));
        flusher.Record("first", 100);
        await flusher.FlushAsync(); // fails for now; the bucket stays

        handler.Status = HttpStatusCode.Accepted;
        flusher.Record("first", 100);
        await flusher.FlushAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"request_count\":2", handler.Bodies[1]);
    }

    [Fact]
    public async Task DropsABatchOnTheFifthFailedFlushInARow()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var logger = new RecordingLogger();
        var configuration = NewConfiguration();
        configuration.Logger = logger;

        await using var flusher = new PerformanceFlusher(configuration, client.Object);
        flusher.Record("GET /posts/{id}", 100);
        for (var i = 0; i < 4; i++) await flusher.FlushAsync();
        Assert.Empty(logger.Messages); // four failures in: still kept

        await flusher.FlushAsync(); // the fifth: dropped
        await flusher.FlushAsync(); // nothing left to send

        client.Verify(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        Assert.Single(logger.Messages, m => m.Contains("delivery failed 5 times in a row"));
    }

    [Fact]
    public async Task ADeliveredFlushStartsTheCountOfFailedFlushesOver()
    {
        var attempts = 0;
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempts == 5); // four failures, one success, then failures again

        await using var flusher = new PerformanceFlusher(NewConfiguration(), client.Object);
        flusher.Record("GET /posts/{id}", 100);
        for (var i = 0; i < 5; i++) await flusher.FlushAsync();
        flusher.Record("GET /posts/{id}", 100);
        for (var i = 0; i < 4; i++) await flusher.FlushAsync();
        Assert.Equal(9, attempts);

        await flusher.FlushAsync(); // only the fifth failure since the success drops it
        await flusher.FlushAsync(); // nothing left to send

        Assert.Equal(10, attempts);
    }

    [Fact]
    public async Task TheBackgroundLoopFlushesOnItsOwnAfterTheConfiguredInterval()
    {
        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .Returns<PerformanceSamplesBatch, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });

        var configuration = NewConfiguration();
        configuration.PerformanceFlushInterval = TimeSpan.FromMilliseconds(20);

        await using var flusher = new PerformanceFlusher(configuration, client.Object);
        flusher.Record("GET /posts/{id}", 100);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var payload = await delivered.Reader.ReadAsync(cts.Token);
        Assert.Single(payload.Samples);
    }

    [Fact]
    public async Task DoesNotRecordWhenTrackPerformanceIsDisabled()
    {
        var configuration = NewConfiguration();
        configuration.TrackPerformance = false;
        var client = new Mock<IClient>();

        await using var flusher = new PerformanceFlusher(configuration, client.Object);
        flusher.Record("GET /posts/{id}", 100);
        await flusher.FlushAsync();

        client.Verify(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IClient> RecordingClient(Channel<PerformanceSamplesBatch> delivered, Action? onDeliver = null)
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .Returns<PerformanceSamplesBatch, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                onDeliver?.Invoke();
                return Task.FromResult(true);
            });
        return client;
    }

    [Fact]
    public async Task DeliversALatencyHistogramPerBucketAlongsideCountSumAndMax()
    {
        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        await using var flusher = new PerformanceFlusher(NewConfiguration(), RecordingClient(delivered).Object);
        foreach (var duration in new double[] { 10, 40, 120, 700, 12_000 })
        {
            flusher.Record("GET /posts", duration);
        }
        await flusher.FlushAsync();

        var sample = Assert.Single((await delivered.Reader.ReadAsync()).Samples);
        Assert.Equal(
            new Dictionary<string, long> { ["50"] = 2, ["250"] = 1, ["1000"] = 1, ["inf"] = 1 },
            sample.Histogram);
        Assert.Equal(sample.RequestCount, sample.Histogram.Values.Sum());
    }

    [Fact]
    public async Task SerializesTheHistogramAsAJsonObjectKeyedByBucketLabel()
    {
        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        await using var flusher = new PerformanceFlusher(NewConfiguration(), RecordingClient(delivered).Object);
        flusher.Record("GET /posts", 70);
        await flusher.FlushAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(await delivered.Reader.ReadAsync());
        Assert.Contains("\"histogram\":{\"100\":1}", json);
    }

    [Fact]
    public async Task KeepsHistogramCountsForTheNextFlushWhenDeliveryFails()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await using var flusher = new PerformanceFlusher(NewConfiguration(), client.Object);
        flusher.Record("GET /posts", 10);
        await flusher.FlushAsync();

        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        client.Setup(c => c.DeliverPerformanceSamplesAsync(It.IsAny<PerformanceSamplesBatch>(), It.IsAny<CancellationToken>()))
            .Returns<PerformanceSamplesBatch, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });
        flusher.Record("GET /posts", 300);
        await flusher.FlushAsync();

        var sample = Assert.Single((await delivered.Reader.ReadAsync()).Samples);
        Assert.Equal(new Dictionary<string, long> { ["50"] = 1, ["500"] = 1 }, sample.Histogram);
    }

    [Fact]
    public async Task ARecordThatLandsDuringDeliveryIsNeverLostAndIsSentOnTheNextFlush()
    {
        var delivered = Channel.CreateUnbounded<PerformanceSamplesBatch>();
        PerformanceFlusher? flusher = null;
        var first = true;
        var client = RecordingClient(delivered, () =>
        {
            if (!first) return;
            first = false;
            // Lands between the snapshot and delivery succeeding: for a bucket already in the
            // snapshot, and for a brand-new one.
            flusher!.Record("GET /posts", 300);
            flusher.Record("GET /new", 5);
        });

        flusher = new PerformanceFlusher(NewConfiguration(), client.Object);
        await using var _ = flusher;
        flusher.Record("GET /posts", 10);
        await flusher.FlushAsync();
        await flusher.FlushAsync();

        var firstBatch = await delivered.Reader.ReadAsync();
        Assert.Equal(new Dictionary<string, long> { ["50"] = 1 }, Assert.Single(firstBatch.Samples).Histogram);

        var second = (await delivered.Reader.ReadAsync()).Samples.ToDictionary(s => s.TransactionName);
        Assert.Equal(1, second["GET /posts"].RequestCount);
        Assert.Equal(new Dictionary<string, long> { ["500"] = 1 }, second["GET /posts"].Histogram);
        Assert.Equal(new Dictionary<string, long> { ["50"] = 1 }, second["GET /new"].Histogram);
    }
}
