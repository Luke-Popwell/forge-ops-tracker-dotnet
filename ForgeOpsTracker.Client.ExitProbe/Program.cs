using ForgeOpsTracker;

// Usage: ExitProbe <dsn> <return|throw|trace|default-environment|development>
// return/throw: captures one exception and then either returns from Main (ProcessExit) or rethrows
// it unhandled (UnhandledException). trace: finishes one TraceAsync trace and returns from Main.
// default-environment: like return, but leaves Environment at its default (whatever the process's
// environment variables resolve to). development: like return, with Environment "Development" and
// Configure called twice, so the not-sending warning can be counted.
// Never flushes on its own.
void Configure() => ForgeOpsTrackerSdk.Configure(config =>
{
    config.Dsn = args[0];
    if (args[1] == "development") config.Environment = "Development";
    else if (args[1] != "default-environment") config.Environment = "Production";
    config.DetectChanges = false;
    config.TrackSessions = false;
    config.TraceCaptureThreshold = TimeSpan.Zero;
});

Configure();
if (args[1] == "development") Configure();

if (args[1] == "trace")
{
    await ForgeOpsTrackerSdk.TraceAsync("job:traced just before exit", async () =>
    {
        await Task.Delay(10);
        return 0;
    });
    return;
}

var error = new InvalidOperationException("reported just before exit");
ForgeOpsTrackerSdk.Capture(error);

if (args[1] == "throw") throw error;
