using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeOpsTracker.Tests;

/// <summary>
/// End-to-end through a real ASP.NET Core pipeline (TestServer), the same technique
/// ForgeOpsTrackerMiddlewareTests/SessionTrackingMiddlewareTests already use. context.User is set
/// directly by a plain inline middleware ahead of UseForgeOpsTrackerUserContext(), standing in for
/// whatever real authentication handler (cookies, JWT, ASP.NET Core Identity) would normally have
/// already populated it by that point in the pipeline: this SDK's own detection only ever reads
/// context.User, so a real auth handler isn't needed to exercise it honestly.
/// </summary>
public class UserContextMiddlewareTests
{
    [Fact]
    public async Task AttachesTheAuthenticatedUserToAReportedException()
    {
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, authenticated: true).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/"));

        var reported = Assert.Single(delivered);
        Assert.NotNull(reported.User);
        Assert.Equal("42", reported.User!["id"]);
        Assert.Equal("ada@example.com", reported.User!["email"]);
        Assert.Equal("ada", reported.User!["username"]);
    }

    [Fact]
    public async Task DoesNotAttachAUserForAnUnauthenticatedRequest()
    {
        List<TrackedEvent> delivered = new();

        using var host = await BuildHost(delivered, authenticated: false).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/"));

        var reported = Assert.Single(delivered);
        Assert.Null(reported.User);
    }

    [Fact]
    public async Task AnExplicitSetUserCallAfterAutoDetectionOverridesItForTheRestOfTheRequest()
    {
        List<TrackedEvent> delivered = new();

        using var host = await new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development");
                    c.DetectChanges = false; // this host delivers through the real Client, so no startup snapshot
                });
                services.AddSingleton<IDeliveryQueue>(new RecordingDeliveryQueue(delivered));
            });
            webHost.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "42") };
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
                    await next();
                });
                app.UseForgeOpsTrackerUserContext();
                app.UseForgeOpsTracker();
                // Application code overriding auto-detection for this one request, e.g. because
                // it knows something about the real customer the auth claims don't capture.
                app.Run(_ =>
                {
                    ForgeOpsTrackerSdk.SetUser(new Dictionary<string, object?> { ["id"] = "99" });
                    throw new InvalidOperationException("boom");
                });
            });
        }).StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GetTestClient().GetAsync("/"));

        var reported = Assert.Single(delivered);
        Assert.Equal("99", reported.User!["id"]);
    }

    private static IHostBuilder BuildHost(List<TrackedEvent> delivered, bool authenticated) =>
        new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddForgeOpsTracker(c =>
                {
                    c.Dsn = "https://key@tracker.example.com/api/v1/events";
                    c.EnabledEnvironments.Add("Development"); // TestServer's default hosting environment
                    c.DetectChanges = false; // this host delivers through the real Client, so no startup snapshot
                });

                services.AddSingleton<IDeliveryQueue>(new RecordingDeliveryQueue(delivered));
            });
            webHost.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    if (authenticated)
                    {
                        var claims = new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, "42"),
                            new Claim(ClaimTypes.Email, "ada@example.com"),
                            new Claim(ClaimTypes.Name, "ada"),
                        };
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
                    }
                    await next();
                });
                app.UseForgeOpsTrackerUserContext();
                app.UseForgeOpsTracker();
                app.Run(_ => throw new InvalidOperationException("boom"));
            });
        });

    private sealed class RecordingDeliveryQueue : IDeliveryQueue
    {
        private readonly List<TrackedEvent> _delivered;
        public RecordingDeliveryQueue(List<TrackedEvent> delivered) => _delivered = delivered;
        public bool Push(TrackedEvent payload)
        {
            _delivered.Add(payload);
            return true;
        }
    }
}
