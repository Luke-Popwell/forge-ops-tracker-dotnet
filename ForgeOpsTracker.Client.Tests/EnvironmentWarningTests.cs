using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker.Tests;

// The once-per-process flag is static, so these run on their own rather than alongside other tests
// that could trip it first. The console (stderr) path is covered end to end in ExitDrainTests.
[CollectionDefinition(nameof(EnvironmentWarningTests), DisableParallelization = true)]
public class EnvironmentWarningCollection
{
}

[Collection(nameof(EnvironmentWarningTests))]
public class EnvironmentWarningTests
{
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Configuration Resolve(Action<Configuration> configure) =>
        new ServiceCollection().AddForgeOpsTracker(configure).BuildServiceProvider().GetRequiredService<Configuration>();

    [Fact]
    public void AddForgeOpsTrackerWarnsOnceThroughTheLoggerWhenADsnIsSetButTheEnvironmentDoesNotSend()
    {
        Configuration.ResetEnvironmentWarningForTesting();
        var logger = new ListLogger();
        void Configure(Configuration c)
        {
            c.Dsn = "https://key@tracker.example.com/api/v1/events";
            c.Environment = "development";
            c.Logger = logger;
        }

        Resolve(Configure);
        Resolve(Configure); // a second container in the same process: still once

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            "[ForgeOps] Not sending: this environment is \"development\", and only production, staging are enabled. "
                + "Set FORGE_OPS_ENVIRONMENT=production (or add \"development\" to the enabled environments) to send from here.",
            entry.Message);
        Configuration.ResetEnvironmentWarningForTesting();
    }

    [Fact]
    public void AddForgeOpsTrackerNeverWarnsWithoutADsnOrInAnEnvironmentThatSends()
    {
        Configuration.ResetEnvironmentWarningForTesting();
        var logger = new ListLogger();

        Resolve(c =>
        {
            c.Dsn = null;
            c.Environment = "development";
            c.Logger = logger;
        });
        Resolve(c =>
        {
            c.Dsn = "https://key@tracker.example.com/api/v1/events";
            c.Environment = "Staging";
            c.Logger = logger;
        });

        Assert.Empty(logger.Entries);
        Configuration.ResetEnvironmentWarningForTesting();
    }
}
