using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Moq;

namespace ForgeOpsTracker.Tests;

// Mirrors the "on process exit" examples in gems/forge_ops_tracker/spec/forge_ops_tracker/delivery_queue_spec.rb.
public class ExitDrainTests
{
    private static Mock<IClient> SlowClient(TimeSpan delay, List<string> delivered)
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverAsync(It.IsAny<TrackedEvent>(), It.IsAny<CancellationToken>()))
            .Returns<TrackedEvent, CancellationToken>(async (payload, _) =>
            {
                await Task.Delay(delay);
                lock (delivered) delivered.Add(payload.ExceptionClass!);
                return true;
            });
        return client;
    }

    [Fact]
    public async Task FlushWaitsUntilEverythingPushedHasBeenDelivered()
    {
        var delivered = new List<string>();
        await using var queue = new DeliveryQueue(new Configuration(), SlowClient(TimeSpan.FromMilliseconds(100), delivered).Object);
        queue.Push(new TrackedEvent { ExceptionClass = "First" });
        queue.Push(new TrackedEvent { ExceptionClass = "Second" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await queue.FlushAsync(cts.Token));

        Assert.Equal(new[] { "First", "Second" }, delivered);
    }

    [Fact]
    public async Task FlushGivesUpWhenCancelledRatherThanWaitingOnAStuckDelivery()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverAsync(It.IsAny<TrackedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<bool>().Task);
        await using var queue = new DeliveryQueue(new Configuration { ShutdownTimeout = TimeSpan.FromMilliseconds(100) }, client.Object);
        queue.Push(new TrackedEvent { ExceptionClass = "Stuck" });

        var stopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.False(await queue.FlushAsync(cts.Token));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FlushReturnsAtOnceWhenNothingWasPushed()
    {
        await using var queue = new DeliveryQueue(new Configuration(), new Mock<IClient>().Object);

        var stopwatch = Stopwatch.StartNew();
        Assert.True(await queue.FlushAsync());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task DrainDeliversWhatIsStillQueuedIncludingTheOneInFlight()
    {
        var delivered = new List<string>();
        var queue = new DeliveryQueue(new Configuration(), SlowClient(TimeSpan.FromMilliseconds(100), delivered).Object);
        queue.Push(new TrackedEvent { ExceptionClass = "First" });
        queue.Push(new TrackedEvent { ExceptionClass = "Second" });

        Assert.True(await queue.DrainAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "First", "Second" }, delivered);
    }

    [Fact]
    public async Task DrainGivesUpAtItsTimeout()
    {
        var delivered = new List<string>();
        var queue = new DeliveryQueue(new Configuration(), SlowClient(TimeSpan.FromMilliseconds(500), delivered).Object);
        for (var n = 0; n < 3; n++) queue.Push(new TrackedEvent { ExceptionClass = $"E{n}" });

        var stopwatch = Stopwatch.StartNew();
        Assert.False(await queue.DrainAsync(TimeSpan.FromMilliseconds(200)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TheExitDrainIsBoundedByTheShorterOfFiveSecondsAndShutdownTimeout()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverAsync(It.IsAny<TrackedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<bool>().Task);
        var queue = new DeliveryQueue(new Configuration { ShutdownTimeout = TimeSpan.FromMilliseconds(200) }, client.Object);
        queue.Push(new TrackedEvent { ExceptionClass = "Stuck" });

        var stopwatch = Stopwatch.StartNew();
        Assert.False(ForgeOpsTrackerSdk.DrainOnExit(new Configuration { ShutdownTimeout = TimeSpan.FromMilliseconds(200) }, queue, null));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(5), DeliveryQueue.ExitDrainTimeout);
    }

    private static TracePayload Trace(string name) =>
        new() { TraceId = name, Spans = new[] { new Span { SpanId = "s", Name = name } } };

    private static Mock<IClient> SlowSpanClient(TimeSpan delay, List<string> delivered)
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverSpansAsync(It.IsAny<TracePayload>(), It.IsAny<CancellationToken>()))
            .Returns<TracePayload, CancellationToken>(async (payload, _) =>
            {
                await Task.Delay(delay);
                lock (delivered) delivered.Add(payload.TraceId!);
                return true;
            });
        return client;
    }

    private static Mock<IClient> StuckClient()
    {
        var client = new Mock<IClient>();
        client.Setup(c => c.DeliverAsync(It.IsAny<TrackedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<bool>().Task);
        client.Setup(c => c.DeliverSpansAsync(It.IsAny<TracePayload>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<bool>().Task);
        return client;
    }

    [Fact]
    public async Task SpanFlushWaitsUntilEveryTracePushedHasBeenDelivered()
    {
        var delivered = new List<string>();
        await using var queue = new SpanQueue(new Configuration(), SlowSpanClient(TimeSpan.FromMilliseconds(100), delivered).Object);
        queue.Push(Trace("first"));
        queue.Push(Trace("second"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await queue.FlushAsync(cts.Token));

        Assert.Equal(new[] { "first", "second" }, delivered);
    }

    [Fact]
    public async Task SpanFlushReturnsAtOnceWhenNothingWasPushed()
    {
        await using var queue = new SpanQueue(new Configuration(), new Mock<IClient>().Object);

        var stopwatch = Stopwatch.StartNew();
        Assert.True(await queue.FlushAsync());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void TheExitDrainDeliversQueuedTracesIncludingTheOneInFlight()
    {
        var delivered = new List<string>();
        var spans = new SpanQueue(new Configuration(), SlowSpanClient(TimeSpan.FromMilliseconds(100), delivered).Object);
        spans.Push(Trace("first"));
        spans.Push(Trace("second"));

        Assert.True(ForgeOpsTrackerSdk.DrainOnExit(new Configuration(), null, spans));
        Assert.Equal(new[] { "first", "second" }, delivered);
    }

    [Fact]
    public void TheExitDrainGivesUpOnTracesAtItsTimeout()
    {
        var configuration = new Configuration { ShutdownTimeout = TimeSpan.FromMilliseconds(200) };
        var spans = new SpanQueue(configuration, StuckClient().Object);
        spans.Push(Trace("stuck"));

        var stopwatch = Stopwatch.StartNew();
        Assert.False(ForgeOpsTrackerSdk.DrainOnExit(configuration, null, spans));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    // Both queues stuck: draining them one after the other would take twice ShutdownTimeout.
    [Fact]
    public void TheExitDrainSharesOneBudgetBetweenErrorsAndTraces()
    {
        var configuration = new Configuration { ShutdownTimeout = TimeSpan.FromMilliseconds(400) };
        var client = StuckClient().Object;
        var errors = new DeliveryQueue(configuration, client);
        var spans = new SpanQueue(configuration, client);
        errors.Push(new TrackedEvent { ExceptionClass = "Stuck" });
        spans.Push(Trace("stuck"));

        var stopwatch = Stopwatch.StartNew();
        Assert.False(ForgeOpsTrackerSdk.DrainOnExit(configuration, errors, spans));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(750), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void TheExitDrainReturnsAtOnceWhenNothingWasCapturedOrTraced()
    {
        var stopwatch = Stopwatch.StartNew();
        Assert.True(ForgeOpsTrackerSdk.DrainOnExit(new Configuration(), null, null));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
    }

    // A real console process using the static ForgeOpsTrackerSdk path, reporting to a local HTTP
    // listener: it captures one exception and ends straight away, by returning from Main
    // (ProcessExit) or by rethrowing it unhandled (UnhandledException).
    [Theory]
    [InlineData("return")]
    [InlineData("throw")]
    public async Task AConsoleAppThatCapturesAndExitsStillDeliversTheEvent(string ending)
    {
        var (exitCode, body) = await RunProbeAsync(ending);

        Assert.Contains("reported just before exit", body);
        if (ending == "return") Assert.Equal(0, exitCode);
        else Assert.NotEqual(0, exitCode);
    }

    // The same, for a console app that wraps its work in TraceAsync and returns from Main.
    [Fact]
    public async Task AConsoleAppThatTracesAndExitsStillDeliversTheTrace()
    {
        var (exitCode, body) = await RunProbeAsync("trace");

        Assert.Contains("job:traced just before exit", body);
        Assert.Equal(0, exitCode);
    }

    // A console app configured with only a DSN, no environment set anywhere: the default is now
    // Production (what ASP.NET Core itself assumes), so it sends. It used to default to Development
    // and silently send nothing.
    [Fact]
    public async Task AConsoleAppWithNoEnvironmentSetSendsAsProduction()
    {
        var probe = await RunProbeRawAsync("default-environment");

        Assert.Equal(0, probe.ExitCode);
        Assert.NotNull(probe.Body);
        Assert.Contains("reported just before exit", probe.Body);
        Assert.Contains("\"environment\":\"Production\"", probe.Body);
        Assert.DoesNotContain("[ForgeOps]", probe.StandardError);
    }

    [Theory]
    [InlineData("FORGE_OPS_ENVIRONMENT", "staging")]
    [InlineData("ASPNETCORE_ENVIRONMENT", "Staging")]
    [InlineData("DOTNET_ENVIRONMENT", "Staging")]
    public async Task AConsoleAppTakesItsEnvironmentFromTheEnvironmentVariables(string variable, string value)
    {
        var probe = await RunProbeRawAsync("default-environment", new Dictionary<string, string> { [variable] = value });

        Assert.NotNull(probe.Body);
        Assert.Contains($"\"environment\":\"{value}\"", probe.Body);
    }

    // With a DSN set and an environment that doesn't send, Configure (called twice here) warns once,
    // on stderr since no Logger is set, and nothing is sent.
    [Fact]
    public async Task AConsoleAppInDevelopmentWarnsOnceOnStderrAndSendsNothing()
    {
        var probe = await RunProbeRawAsync("development");

        Assert.Equal(0, probe.ExitCode);
        Assert.Null(probe.Body);
        var warnings = probe.StandardError.Split('\n').Where(line => line.Contains("[ForgeOps]")).ToList();
        Assert.Single(warnings);
        Assert.Equal(
            "[ForgeOps] Not sending: this environment is \"Development\", and only production, staging are enabled. "
                + "Set FORGE_OPS_ENVIRONMENT=production (or add \"Development\" to the enabled environments) to send from here.",
            warnings[0].TrimEnd('\r'));
    }

    private static async Task<(int ExitCode, string Body)> RunProbeAsync(string ending)
    {
        var probe = await RunProbeRawAsync(ending);
        Assert.True(probe.Body is not null, $"nothing arrived (exit code {probe.ExitCode}): {probe.StandardError}");
        return (probe.ExitCode, probe.Body!);
    }

    private sealed record ProbeRun(int ExitCode, string? Body, string StandardError);

    // Runs the probe with FORGE_OPS_ENVIRONMENT, ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT cleared
    // (then set from `environment`), and returns what arrived within a second of it exiting, if anything.
    private static async Task<ProbeRun> RunProbeRawAsync(string ending, IDictionary<string, string>? environment = null)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = ReceiveOneAsync(listener);

        var probe = Path.Combine(AppContext.BaseDirectory, "ForgeOpsTracker.Client.ExitProbe.dll");
        var start = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add($"http://key@127.0.0.1:{port}/api/v1/events");
        start.ArgumentList.Add(ending);
        foreach (var name in new[] { "FORGE_OPS_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
        {
            start.Environment.Remove(name);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), "the probe did not exit");

        var finished = await Task.WhenAny(received, Task.Delay(TimeSpan.FromSeconds(1)));
        return new ProbeRun(process.ExitCode, finished == received ? await received : null, await stderr);
    }

    private static async Task<string> ReceiveOneAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = 202;
        context.Response.Close();
        return body;
    }

    private static int FreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    // The dotnet host running these tests, so the probe runs on the same runtime.
    private static string DotnetHost()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath)) return hostPath;

        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current) == "dotnet") return current;

        return "dotnet";
    }
}
