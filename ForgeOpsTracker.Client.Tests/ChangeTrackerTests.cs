using System.Collections;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

public class ChangeTrackerTests
{
    private static Configuration NewConfiguration() => new()
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        EnabledEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Production" },
        Environment = "Production"
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) =>
            _respond = respond ?? (_ => new HttpResponseMessage(HttpStatusCode.Accepted));

        public List<(string Path, string? Auth, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return _respond(request);
        }
    }

    // A fresh once-flag per tracker, so these tests never race the process-wide one other tests use.
    private static ChangeTracker Tracker(Configuration configuration, FakeHandler handler) =>
        new(configuration, new Client(configuration, new HttpClient(handler)), new OnceFlag());

    [Fact]
    public async Task RecordChangePostsTheDocumentedPayloadToTheChangesEndpoint()
    {
        var handler = new FakeHandler();
        var tracker = Tracker(NewConfiguration(), handler);

        Assert.True(tracker.RecordChange(
            ChangeKind.FeatureFlag,
            "Enabled new checkout",
            new Dictionary<string, object?> { ["flag"] = "new_checkout", ["enabled"] = true },
            service: "web",
            actor: "luke",
            url: "https://example.com/flags/1",
            id: "flag-1",
            occurredAt: new DateTimeOffset(2026, 9, 25, 12, 30, 0, TimeSpan.FromHours(2))));
        await tracker.DisposeAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/changes", request.Path);
        Assert.Equal("Bearer key", request.Auth);

        var json = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("feature_flag", json.GetProperty("kind").GetString());
        Assert.Equal("Enabled new checkout", json.GetProperty("title").GetString());
        Assert.Equal("new_checkout", json.GetProperty("details").GetProperty("flag").GetString());
        Assert.True(json.GetProperty("details").GetProperty("enabled").GetBoolean());
        Assert.Equal("Production", json.GetProperty("environment").GetString());
        Assert.Equal("web", json.GetProperty("service").GetString());
        Assert.Equal("luke", json.GetProperty("actor").GetString());
        Assert.Equal("https://example.com/flags/1", json.GetProperty("url").GetString());
        Assert.Equal("flag-1", json.GetProperty("id").GetString());
        Assert.Equal("2026-09-25T10:30:00Z", json.GetProperty("occurred_at").GetString());
    }

    [Fact]
    public async Task OptionalFieldsAreOmittedAndDefaultsFilledIn()
    {
        var handler = new FakeHandler();
        var tracker = Tracker(NewConfiguration(), handler);

        tracker.RecordChange(ChangeKind.Migration, "AddIndexToOrders");
        await tracker.DisposeAsync();

        var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement;
        var keys = json.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "environment", "kind", "occurred_at", "title" }, keys);
        Assert.Equal("Production", json.GetProperty("environment").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", json.GetProperty("occurred_at").GetString());
    }

    [Theory]
    [InlineData("feature_flag", "feature_flag")]
    [InlineData("config", "config")]
    [InlineData("migration", "migration")]
    [InlineData("dependency", "dependency")]
    [InlineData("infrastructure", "infrastructure")]
    [InlineData("other", "other")]
    [InlineData(" Config ", "config")]
    [InlineData("deploy", "other")]
    [InlineData("", "other")]
    [InlineData(null, "other")]
    public void KindIsValidatedAndDefaultsToOther(string? kind, string expected) =>
        Assert.Equal(expected, ChangeTracker.NormalizeKind(kind));

    [Fact]
    public async Task AnUnknownKindIsSentAsOther()
    {
        var handler = new FakeHandler();
        var tracker = Tracker(NewConfiguration(), handler);

        tracker.RecordChange("rollout", "Something");
        await tracker.DisposeAsync();

        Assert.Equal("other", JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TitleIsRequiredAndCutTo200Characters()
    {
        var handler = new FakeHandler();
        var tracker = Tracker(NewConfiguration(), handler);

        Assert.False(tracker.RecordChange(ChangeKind.Config, "   "));
        Assert.True(tracker.RecordChange(ChangeKind.Config, new string('x', 250)));
        await tracker.DisposeAsync();

        var title = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement.GetProperty("title").GetString();
        Assert.Equal(ChangeTracker.MaxTitleLength, title!.Length);
    }

    [Fact]
    public async Task DetailsAreScrubbedForLikelyPii()
    {
        var handler = new FakeHandler();
        var tracker = Tracker(NewConfiguration(), handler);

        tracker.RecordChange(ChangeKind.Config, "Rotated", new Dictionary<string, object?> { ["api_token"] = "abc", ["owner"] = "a@b.co" });
        await tracker.DisposeAsync();

        var details = JsonDocument.Parse(Assert.Single(handler.Requests).Body).RootElement.GetProperty("details");
        Assert.Equal(PiiScrubber.Redacted, details.GetProperty("api_token").GetString());
        Assert.DoesNotContain("a@b.co", details.GetProperty("owner").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NeverThrowsOnAnErrorResponse(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status));
        var tracker = Tracker(NewConfiguration(), handler);

        Assert.True(tracker.RecordChange(ChangeKind.Config, "Changed"));
        await tracker.DisposeAsync();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NeverThrowsWhenTheNetworkFailsAndKeepsDeliveringAfterwards()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException("down")
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        var tracker = Tracker(NewConfiguration(), handler);

        tracker.RecordChange(ChangeKind.Config, "first");
        tracker.RecordChange(ChangeKind.Config, "second");
        await tracker.DisposeAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public void IsANoOpWhenNotEnabled()
    {
        var configuration = NewConfiguration();
        configuration.Dsn = null;
        var handler = new FakeHandler();
        var tracker = Tracker(configuration, handler);

        Assert.False(tracker.RecordChange(ChangeKind.Config, "Changed"));
        Assert.False(tracker.SendStartupSnapshot());
    }

    [Fact]
    public void TheStaticFacadeIsANoOpWithoutADsn()
    {
        // The ambient configuration has no DSN in the test process.
        Assert.False(ForgeOpsTrackerSdk.RecordChange(ChangeKind.Config, "Changed"));
    }

    [Fact]
    public async Task TheStartupSnapshotPostsTheRuntimeOnlyOnceAndNoEnvNamesByDefault()
    {
        var handler = new FakeHandler();
        var tracker = Tracker(NewConfiguration(), handler);

        Assert.True(tracker.SendStartupSnapshot());
        Assert.False(tracker.SendStartupSnapshot());
        await tracker.DisposeAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/change_snapshots", request.Path);
        Assert.Equal("Bearer key", request.Auth);

        var json = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("Production", json.GetProperty("environment").GetString());
        Assert.False(json.TryGetProperty("service", out _));
        var state = json.GetProperty("state");
        Assert.StartsWith(".net ", state.GetProperty("runtime").GetString());
        Assert.Equal(new[] { "runtime" }, state.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task DetectChangesFalseSendsNothing()
    {
        var configuration = NewConfiguration();
        configuration.DetectChanges = false;
        var handler = new FakeHandler();
        var tracker = Tracker(configuration, handler);

        Assert.False(tracker.SendStartupSnapshot());
        await tracker.DisposeAsync();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void EnvVarNamesAreSentOnlyWhenOptedInWithTheDenylistApplied()
    {
        var environment = new Hashtable
        {
            ["DATABASE_URL"] = "postgres://secret",
            ["STRIPE_API_KEY"] = "sk_live_secret",
            ["HOSTNAME"] = "web-1",
            ["PATH"] = "/usr/bin",
            ["LC_ALL"] = "C",
            ["KUBERNETES_SERVICE_HOST"] = "10.0.0.1",
            ["REDIS_SERVICE_HOST"] = "10.0.0.2",
            ["REDIS_SERVICE_PORT_HTTP"] = "6379",
            ["REDIS_PORT_6379_TCP_ADDR"] = "10.0.0.2",
            ["SYSTEMD_EXEC_PID"] = "1",
            ["FORGE_OPS_DSN"] = "https://key@example.com",
            ["_"] = "/usr/bin/dotnet"
        };

        var configuration = NewConfiguration();
        Assert.Null(ChangeTracker.BuildSnapshot(configuration, environment).State.EnvVarNames);

        configuration.TrackEnvVarNames = true;
        var snapshot = ChangeTracker.BuildSnapshot(configuration, environment);
        Assert.Equal(new[] { "DATABASE_URL", "STRIPE_API_KEY" }, snapshot.State.EnvVarNames);

        var body = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("dependencies", body);
        Assert.DoesNotContain("schema_version", body);
    }

    [Theory]
    [InlineData("HOME", true)]
    [InlineData("Path", true)]
    [InlineData("TZ", true)]
    [InlineData("PORT", true)]
    [InlineData("DYNO", true)]
    [InlineData("INVOCATION_ID", true)]
    [InlineData("JOURNAL_STREAM", true)]
    [InlineData("MEMORY_PRESSURE_WATCH", true)]
    [InlineData("LC_CTYPE", true)]
    [InlineData("FORGE_OPS_RELEASE", true)]
    [InlineData("API_SERVICE_PORT", true)]
    [InlineData("API_PORT_80_TCP", true)]
    [InlineData("DATABASE_URL", false)]
    [InlineData("REPORT_TITLE", false)]
    [InlineData("SUPPORT_EMAIL", false)]
    public void TheDenylistDropsHostNoiseOnly(string name, bool noise) =>
        Assert.Equal(noise, EnvVarNames.IsNoise(name));

    [Fact]
    public async Task AddForgeOpsTrackerRegistersAnInjectableChangeTracker()
    {
        var client = new RecordingClient();
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddForgeOpsTracker(c =>
                    {
                        c.Dsn = "https://key@tracker.example.com/api/v1/events";
                        c.EnabledEnvironments.Add("Development");
                    });
                    services.AddSingleton<IClient>(client);
                });
                webHost.Configure(app => app.Run(_ => Task.CompletedTask));
            })
            .StartAsync();

        host.Services.GetRequiredService<ChangeTracker>().RecordChange(ChangeKind.Infrastructure, "Scaled to 4 workers");

        var change = await client.Changes.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("infrastructure", change.Kind);
        Assert.Equal(new Configuration().Environment, change.Environment); // the default, from the environment variables
    }

    private sealed class RecordingClient : IClient
    {
        public TaskCompletionSource<ChangeRecord> Changes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> DeliverAsync(TrackedEvent payload, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> DeliverChangeAsync(ChangeRecord payload, CancellationToken cancellationToken = default)
        {
            Changes.TrySetResult(payload);
            return Task.FromResult(true);
        }
    }
}
