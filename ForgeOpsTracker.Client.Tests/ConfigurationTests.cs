namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/configuration_spec.rb.
public class ConfigurationTests
{
    private static Configuration NewConfiguration() => new()
    {
        Dsn = null,
        EnabledEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Production", "Staging" },
        Environment = "Production"
    };

    [Fact]
    public void ExtractsApiKeyAndCredentialFreeIngestionUriFromDsn()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = "https://secret-key@tracker.example.com/api/v1/events";

        Assert.Equal("secret-key", configuration.ApiKey);
        Assert.Equal("https://tracker.example.com/api/v1/events", configuration.IngestionUri()?.ToString());
    }

    [Fact]
    public void ReturnsNullForBothWhenThereIsNoDsn()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = null;

        Assert.Null(configuration.ApiKey);
        Assert.Null(configuration.IngestionUri());
    }

    [Fact]
    public void ReturnsNullForBothWhenTheDsnIsMalformed()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = "not a uri :: at all";

        Assert.Null(configuration.ApiKey);
        Assert.Null(configuration.IngestionUri());
    }

    [Fact]
    public void IsEnabledWithAValidDsnInAnEnabledEnvironment()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = "https://key@tracker.example.com/api/v1/events";
        configuration.Environment = "Production";

        Assert.True(configuration.IsEnabled());
    }

    [Fact]
    public void IsNotEnabledWithNoDsnConfigured()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = null;

        Assert.False(configuration.IsEnabled());
    }

    [Fact]
    public void IsNotEnabledWhenTheDsnHasNoApiKey()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = "https://tracker.example.com/api/v1/events";

        Assert.False(configuration.IsEnabled());
    }

    [Fact]
    public void IsNotEnabledOutsideTheConfiguredEnabledEnvironments()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = "https://key@tracker.example.com/api/v1/events";
        configuration.Environment = "Development";

        Assert.False(configuration.IsEnabled());
    }

    [Fact]
    public void EnvironmentIsForgeOpsEnvironmentThenAspNetCoreThenDotNetElseProduction()
    {
        string Resolve(Dictionary<string, string?> variables) =>
            Configuration.ResolveEnvironment(name => variables.TryGetValue(name, out var value) ? value : null);

        Assert.Equal("Production", Resolve(new()));
        Assert.Equal("Staging", Resolve(new() { ["DOTNET_ENVIRONMENT"] = "Staging" }));
        Assert.Equal("Development", Resolve(new() { ["DOTNET_ENVIRONMENT"] = "Staging", ["ASPNETCORE_ENVIRONMENT"] = "Development" }));
        Assert.Equal("production", Resolve(new() { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["FORGE_OPS_ENVIRONMENT"] = "production" }));
        Assert.Equal("Production", Resolve(new() { ["FORGE_OPS_ENVIRONMENT"] = " ", ["ASPNETCORE_ENVIRONMENT"] = "" }));
    }

    [Fact]
    public void NotSendingWarningOnlyWithADsnAndAnEnvironmentThatDoesNotSend()
    {
        var configuration = NewConfiguration();
        configuration.Environment = "development";
        Assert.Null(configuration.NotSendingWarning()); // no DSN

        configuration.Dsn = "https://key@tracker.example.com/api/v1/events";
        Assert.Equal(
            "[ForgeOps] Not sending: this environment is \"development\", and only production, staging are enabled. "
                + "Set FORGE_OPS_ENVIRONMENT=production (or add \"development\" to the enabled environments) to send from here.",
            configuration.NotSendingWarning());

        configuration.Environment = "staging";
        Assert.Null(configuration.NotSendingWarning());
    }

    [Fact]
    public void CaptureSourceContextDefaultsToTrue()
    {
        var configuration = new Configuration();

        Assert.True(configuration.CaptureSourceContext);
    }

    [Fact]
    public void TrackSessionsDefaultsToTrue()
    {
        var configuration = new Configuration();

        Assert.True(configuration.TrackSessions);
    }

    [Fact]
    public void SessionFlushIntervalDefaultsTo60Seconds()
    {
        var configuration = new Configuration();

        Assert.Equal(TimeSpan.FromSeconds(60), configuration.SessionFlushInterval);
    }

    [Fact]
    public void SessionCheckinsUriSwapsEventsForSessionCheckins()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = "https://secret-key@tracker.example.com/api/v1/events";

        Assert.Equal("https://tracker.example.com/api/v1/session_checkins", configuration.SessionCheckinsUri()?.ToString());
    }

    [Fact]
    public void SessionCheckinsUriIsNullWithNoDsn()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = null;

        Assert.Null(configuration.SessionCheckinsUri());
    }

    [Fact]
    public void TrackPerformanceDefaultsToTrue()
    {
        var configuration = new Configuration();

        Assert.True(configuration.TrackPerformance);
    }

    [Fact]
    public void PerformanceFlushIntervalDefaultsTo60Seconds()
    {
        var configuration = new Configuration();

        Assert.Equal(TimeSpan.FromSeconds(60), configuration.PerformanceFlushInterval);
    }

    [Fact]
    public void PerformanceSamplesUriSwapsEventsForPerformanceSamples()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = "https://secret-key@tracker.example.com/api/v1/events";

        Assert.Equal("https://tracker.example.com/api/v1/performance_samples", configuration.PerformanceSamplesUri()?.ToString());
    }

    [Fact]
    public void PerformanceSamplesUriIsNullWithNoDsn()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = null;

        Assert.Null(configuration.PerformanceSamplesUri());
    }
}
