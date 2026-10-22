using System.Net;
using System.Threading.Channels;
using Moq;

namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/session_flusher_spec.rb.
public class SessionFlusherTests
{
    private static Configuration NewConfiguration() => new()
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        EnabledEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Production" },
        Environment = "Production",
        Release = "1.2.3",
        // Long enough that no test relying on FlushAsync's own explicit call also races a real
        // background tick; the interval-driven test below overrides this down to something
        // short instead.
        SessionFlushInterval = TimeSpan.FromMinutes(10)
    };

    [Fact]
    public async Task AggregatesCountsAndDeliversASnapshotResettingAfterASuccessfulFlush()
    {
        var delivered = Channel.CreateUnbounded<SessionCheckin>();
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()))
            .Returns<SessionCheckin, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });

        await using var flusher = new SessionFlusher(NewConfiguration(), client.Object);
        flusher.RecordSession(false);
        flusher.RecordSession(true);
        flusher.RecordSession(false);
        await flusher.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var payload = await delivered.Reader.ReadAsync(cts.Token);
        Assert.Equal(3, payload.SessionsCount);
        Assert.Equal(1, payload.CrashedSessionsCount);
        Assert.Equal("1.2.3", payload.Release);
        Assert.Equal("Production", payload.Environment);

        // Nothing new was recorded since the successful flush above, so this one sends nothing.
        await flusher.FlushAsync();
        Assert.False(delivered.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DoesNothingOnAFlushWithNoSessionsRecorded()
    {
        var client = new Mock<IClient>();

        await using var flusher = new SessionFlusher(NewConfiguration(), client.Object);
        await flusher.FlushAsync();

        client.Verify(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KeepsTheCountsForTheNextFlushInsteadOfLosingThemWhenDeliveryFails()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await using var flusher = new SessionFlusher(NewConfiguration(), client.Object);
        flusher.RecordSession(false);
        await flusher.FlushAsync(); // fails; the count must not be reset

        var delivered = Channel.CreateUnbounded<SessionCheckin>();
        client.Setup(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()))
            .Returns<SessionCheckin, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });
        flusher.RecordSession(false);
        await flusher.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await delivered.Reader.ReadAsync(cts.Token);
        Assert.Equal(2, result.SessionsCount);
    }

    [Fact]
    public async Task DropsACheckinTheServerRejectsAndNeverSendsItAgain()
    {
        var handler = new StatusHandler(HttpStatusCode.UnprocessableEntity);
        var logger = new RecordingLogger();
        var configuration = NewConfiguration();
        configuration.Logger = logger;

        await using var flusher = new SessionFlusher(configuration, new Client(configuration, new HttpClient(handler)));
        flusher.RecordSession(false);
        flusher.RecordSession(true);
        await flusher.FlushAsync(); // rejected: the counts are let go
        await flusher.FlushAsync(); // nothing left to send

        Assert.Single(handler.Bodies);
        Assert.Single(logger.Messages, m => m.Contains("dropped a session checkin of 2 sessions: the server rejected it"));

        handler.Status = HttpStatusCode.Accepted;
        flusher.RecordSession(false);
        await flusher.FlushAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"sessions_count\":1", handler.Bodies[1]);
        Assert.Contains("\"crashed_sessions_count\":0", handler.Bodies[1]);
    }

    [Fact]
    public async Task KeepsTheCountsWhenTheServerAnswersWithA5xx()
    {
        var handler = new StatusHandler(HttpStatusCode.InternalServerError);
        var configuration = NewConfiguration();

        await using var flusher = new SessionFlusher(configuration, new Client(configuration, new HttpClient(handler)));
        flusher.RecordSession(false);
        await flusher.FlushAsync(); // fails for now; the count stays

        handler.Status = HttpStatusCode.Accepted;
        flusher.RecordSession(false);
        await flusher.FlushAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"sessions_count\":2", handler.Bodies[1]);
    }

    [Fact]
    public async Task DropsTheCountsOnTheFifthFailedFlushInARow()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var logger = new RecordingLogger();
        var configuration = NewConfiguration();
        configuration.Logger = logger;

        await using var flusher = new SessionFlusher(configuration, client.Object);
        flusher.RecordSession(false);
        for (var i = 0; i < 4; i++) await flusher.FlushAsync();
        Assert.Empty(logger.Messages); // four failures in: still kept

        await flusher.FlushAsync(); // the fifth: dropped
        await flusher.FlushAsync(); // nothing left to send

        client.Verify(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        Assert.Single(logger.Messages, m => m.Contains("delivery failed 5 times in a row"));
    }

    [Fact]
    public async Task TheBackgroundLoopFlushesOnItsOwnAfterTheConfiguredInterval()
    {
        var delivered = Channel.CreateUnbounded<SessionCheckin>();
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()))
            .Returns<SessionCheckin, CancellationToken>((payload, _) =>
            {
                delivered.Writer.TryWrite(payload);
                return Task.FromResult(true);
            });

        var configuration = NewConfiguration();
        configuration.SessionFlushInterval = TimeSpan.FromMilliseconds(20);

        await using var flusher = new SessionFlusher(configuration, client.Object);
        flusher.RecordSession(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var payload = await delivered.Reader.ReadAsync(cts.Token);
        Assert.Equal(1, payload.SessionsCount);
    }

    [Fact]
    public async Task DoesNotRecordWhenTrackSessionsIsDisabled()
    {
        var configuration = NewConfiguration();
        configuration.TrackSessions = false;
        var client = new Mock<IClient>();

        await using var flusher = new SessionFlusher(configuration, client.Object);
        flusher.RecordSession(false);
        await flusher.FlushAsync();

        client.Verify(c => c.DeliverSessionCheckinAsync(It.IsAny<SessionCheckin>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
