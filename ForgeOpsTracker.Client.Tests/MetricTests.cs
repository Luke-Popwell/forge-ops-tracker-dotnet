using System.Net;
using System.Text.Json;
using Moq;

namespace ForgeOpsTracker.Tests;

public class MetricTests
{
    private static Configuration NewConfiguration() => new()
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        EnabledEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Production" },
        Environment = "Production",
        Release = "1.2.3",
        ServerName = "web-1",
        MetricFlushInterval = TimeSpan.FromHours(1),
        InfrastructureMetricFlushInterval = TimeSpan.FromHours(1)
    };

    private static CustomMetricEntry Entry(string name, double value = 1) => new() { MetricName = name, Value = value };

    private static MetricBuffer<CustomMetricEntry> Buffer(Func<IReadOnlyList<CustomMetricEntry>, Task<bool>> deliver) =>
        new(NewConfiguration(), (entries, _) => deliver(entries), () => TimeSpan.FromHours(1));

    [Fact]
    public async Task DeliversEveryEntryAsOneBatch()
    {
        var delivered = new List<List<string>>();
        await using var buffer = Buffer(entries =>
        {
            delivered.Add(entries.Select(e => e.MetricName).ToList());
            return Task.FromResult(true);
        });

        buffer.Record(Entry("signup"), 1);
        buffer.Record(Entry("payment", 49.5), 49.5);
        await buffer.FlushAsync();

        Assert.Equal(new[] { new List<string> { "signup", "payment" } }, delivered);
    }

    [Fact]
    public void DropsNaNAndInfiniteValuesSoOneBadEntryCannotPoisonABatch()
    {
        var buffer = Buffer(_ => Task.FromResult(true));

        Assert.False(buffer.Record(Entry("a"), double.NaN));
        Assert.False(buffer.Record(Entry("b"), double.PositiveInfinity));
        Assert.True(buffer.Record(Entry("c", -12), -12)); // a refund is a real metric
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public async Task AFailedDeliveryKeepsEveryEntryForTheNextFlush()
    {
        var outcomes = new Queue<bool>(new[] { false, true });
        var delivered = new List<List<string>>();
        await using var buffer = Buffer(entries =>
        {
            delivered.Add(entries.Select(e => e.MetricName).ToList());
            return Task.FromResult(outcomes.Dequeue());
        });

        buffer.Record(Entry("a"), 1);
        await buffer.FlushAsync();
        buffer.Record(Entry("b"), 1);
        await buffer.FlushAsync();
        await buffer.FlushAsync(); // nothing left: no third delivery

        Assert.Equal(new[] { new List<string> { "a" }, new List<string> { "a", "b" } }, delivered);
    }

    [Fact]
    public async Task ABatchTheServerRejectsIsDroppedNeverSentAgainAndLeavesRoomForNewMetrics()
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        var logger = new RecordingLogger();
        var configuration = NewConfiguration();
        configuration.Logger = logger;
        await using var reporter = new MetricsReporter(configuration, new Client(configuration, new HttpClient(handler)));

        for (var i = 0; i < MetricBuffer<CustomMetricEntry>.MaxEntries + 10; i++) reporter.CaptureMetric("signup"); // fills the buffer; the last ten are refused
        await reporter.FlushAsync(); // rejected: the batch is let go
        await reporter.FlushAsync(); // nothing left to send

        Assert.Single(handler.Bodies);
        Assert.Single(logger.Messages, m => m.Contains("dropped 1000 metrics: the server rejected it"));

        handler.Status = HttpStatusCode.Accepted;
        reporter.CaptureMetric("payment"); // kept again: the rejected batch no longer fills the buffer
        await reporter.FlushAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"metric_name\":\"payment\"", handler.Bodies[1]);
        Assert.DoesNotContain("signup", handler.Bodies[1]);
    }

    [Fact]
    public async Task ABatchTheServerAnswersWithA5xxIsKeptForTheNextFlush()
    {
        var handler = new StatusHandler(HttpStatusCode.BadGateway);
        var configuration = NewConfiguration();
        await using var reporter = new MetricsReporter(configuration, new Client(configuration, new HttpClient(handler)));

        reporter.CaptureMetric("signup");
        await reporter.FlushAsync(); // fails for now; the entry stays

        handler.Status = HttpStatusCode.Accepted;
        reporter.CaptureMetric("payment");
        await reporter.FlushAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"metric_name\":\"signup\"", handler.Bodies[1]);
        Assert.Contains("\"metric_name\":\"payment\"", handler.Bodies[1]);
    }

    [Fact]
    public async Task TheFifthFailedFlushInARowDropsTheBatch()
    {
        var attempts = 0;
        await using var buffer = Buffer(_ =>
        {
            attempts++;
            return Task.FromResult(false);
        });

        buffer.Record(Entry("a"), 1);
        for (var i = 0; i < 4; i++) await buffer.FlushAsync();
        Assert.Equal(1, buffer.Count); // four failures in: still kept

        await buffer.FlushAsync(); // the fifth: dropped
        await buffer.FlushAsync(); // nothing left to send

        Assert.Equal(5, attempts);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public async Task AnEntryRecordedWhileDeliveryIsInFlightIsNeverLost()
    {
        var inFlight = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var delivered = new List<List<string>>();
        await using var buffer = Buffer(async entries =>
        {
            delivered.Add(entries.Select(e => e.MetricName).ToList());
            if (delivered.Count == 1)
            {
                inFlight.SetResult();
                await release.Task;
            }
            return true;
        });

        buffer.Record(Entry("first"), 1);
        var flushing = buffer.FlushAsync();
        await inFlight.Task;
        buffer.Record(Entry("during"), 1);
        release.SetResult();
        await flushing;
        await buffer.FlushAsync();

        Assert.Equal(new[] { new List<string> { "first" }, new List<string> { "during" } }, delivered);
    }

    [Fact]
    public async Task IsCappedAndDropsFurtherEntriesUntilAFlushSucceeds()
    {
        await using var buffer = Buffer(_ => Task.FromResult(false));
        var accepted = 0;
        for (var i = 0; i < MetricBuffer<CustomMetricEntry>.MaxEntries + 50; i++)
        {
            if (buffer.Record(Entry("m"), 1)) accepted++;
        }

        Assert.Equal(MetricBuffer<CustomMetricEntry>.MaxEntries, accepted);
    }

    [Fact]
    public async Task TheBackgroundLoopFlushesOnItsOwnAfterTheConfiguredInterval()
    {
        var delivered = new TaskCompletionSource<int>();
        var configuration = NewConfiguration();
        await using var buffer = new MetricBuffer<CustomMetricEntry>(configuration, (entries, _) =>
        {
            delivered.TrySetResult(entries.Count);
            return Task.FromResult(true);
        }, () => TimeSpan.FromMilliseconds(50));

        buffer.Record(Entry("a"), 1);

        Assert.Equal(1, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Path, string? Auth, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public async Task CaptureMetricAndCaptureInfrastructureMetricDeliverToTheirOwnEndpointsEndToEnd()
    {
        var handler = new FakeHandler();
        var configuration = NewConfiguration();
        await using var reporter = new MetricsReporter(configuration, new Client(configuration, new HttpClient(handler)));

        reporter.CaptureMetric("signup");
        reporter.CaptureMetric("payment", 49);
        reporter.CaptureInfrastructureMetric("cpu", 0.42, "db-1");
        reporter.CaptureInfrastructureMetric("memory", 0.7);
        await reporter.FlushAsync();

        var custom = handler.Requests.Single(r => r.Path == "/api/v1/custom_metrics");
        Assert.Equal("Bearer key", custom.Auth);
        var customMetrics = JsonDocument.Parse(custom.Body).RootElement.GetProperty("metrics");
        Assert.Equal("signup", customMetrics[0].GetProperty("metric_name").GetString());
        Assert.Equal(1, customMetrics[0].GetProperty("value").GetDouble());
        Assert.Equal(49, customMetrics[1].GetProperty("value").GetDouble());
        Assert.Equal("Production", customMetrics[0].GetProperty("environment").GetString());
        Assert.Equal("1.2.3", customMetrics[0].GetProperty("release").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", customMetrics[0].GetProperty("recorded_at").GetString());

        var infrastructure = JsonDocument.Parse(handler.Requests.Single(r => r.Path == "/api/v1/infrastructure_metrics").Body).RootElement.GetProperty("metrics");
        Assert.Equal("db-1", infrastructure[0].GetProperty("hostname").GetString());
        Assert.Equal("web-1", infrastructure[1].GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task CaptureMetricIsANoOpWhenTheClientIsNotEnabled()
    {
        var client = new Mock<IClient>();
        var configuration = NewConfiguration();
        configuration.Environment = "Development";
        await using var reporter = new MetricsReporter(configuration, client.Object);

        reporter.CaptureMetric("signup");
        reporter.CaptureInfrastructureMetric("cpu", 1);
        await reporter.FlushAsync();

        client.Verify(c => c.DeliverMetricsAsync(It.IsAny<CustomMetricsBatch>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.DeliverInfrastructureMetricsAsync(It.IsAny<InfrastructureMetricsBatch>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void TheMetricUrisSwapTheTrailingEventsSegment()
    {
        var configuration = NewConfiguration();
        Assert.Equal("https://tracker.example.com/api/v1/custom_metrics", configuration.CustomMetricsUri()!.ToString());
        Assert.Equal("https://tracker.example.com/api/v1/infrastructure_metrics", configuration.InfrastructureMetricsUri()!.ToString());
    }
}
