using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Moq;

namespace ForgeOpsTracker.Tests;

// Checks this client against Fixtures/strict_privacy_vectors.json (a byte-identical copy of
// sdks/strict_privacy_vectors.json, generated from the server's own StrictPrivacy), and that strict
// privacy strips every free-text field from an event while leaving events exactly as before when
// it's off.
public class StrictPrivacyTests
{
    private const string EmailMessage = "Couldn't find patient jane@example.com with id=8821";

    // The fingerprint is taken from the message as it would otherwise have been sent: scrubbed.
    private const string ScrubbedMessage = "Couldn't find patient [EMAIL FILTERED] with id=8821";

    private static JsonElement Vectors(string kind)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "strict_privacy_vectors.json");
        return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.GetProperty(kind);
    }

    private static Configuration NewConfiguration() => new()
    {
        Dsn = "https://abc123@example.com/api/v1/events",
        Environment = "production",
        Release = "1.2.3",
        ServerName = "web-1",
        StrictPrivacy = false,
        PrivacyAllowedFields = new HashSet<string>(),
        CaptureSqlStatement = true,
        AppAssemblies = new HashSet<System.Reflection.Assembly> { typeof(StrictPrivacyTests).Assembly }
    };

    private static Exception RaiseAndCapture()
    {
        try
        {
            var error = new InvalidOperationException(EmailMessage);
            error.Data[SqlStatement.ExplicitDataKey] = "SELECT * FROM patients WHERE id = 8821";
            throw error;
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    private static (TrackedEvent Event, int Reads) Build(Configuration configuration)
    {
        var reads = 0;
        var builder = new EventBuilder(configuration, _ =>
        {
            reads++;
            return Enumerable.Range(0, 5000).Select(i => $"line {i}").ToArray();
        });
        var evt = builder.Build(
            RaiseAndCapture(),
            new Dictionary<string, object?>
            {
                ["error_code"] = "E42",
                ["retries"] = 3,
                ["flagged"] = true,
                ["nested"] = new Dictionary<string, object?> { ["a"] = 1 },
                ["note"] = "free text"
            },
            new Dictionary<string, object?> { ["id"] = 42, ["email"] = "jane@example.com", ["username"] = "jane" },
            new List<BreadcrumbEntry> { new() { Category = "custom", Message = "opened jane's chart" } });
        return (evt, reads);
    }

    [Fact]
    public void NormalizesAndFingerprintsEveryVectorExactlyAsTheServerDoes()
    {
        var vectors = Vectors("fingerprint").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            var message = vector.GetProperty("message").GetString();
            Assert.Equal(vector.GetProperty("normalized").GetString(), StrictPrivacy.NormalizeMessage(message));
            Assert.Equal(vector.GetProperty("fingerprint").GetString(), StrictPrivacy.MessageFingerprint(message));
        }
    }

    [Fact]
    public void AnAbsentMessageFingerprintsAsTheEmptyOne()
    {
        Assert.Equal(StrictPrivacy.MessageFingerprint(""), StrictPrivacy.MessageFingerprint(null));
    }

    [Fact]
    public void TakesIdsOutOfEveryPathVector()
    {
        foreach (var vector in Vectors("path").EnumerateArray())
        {
            Assert.Equal(vector.GetProperty("strict").GetString(), StrictPrivacy.StrictPath(vector.GetProperty("path").GetString()));
        }
    }

    [Fact]
    public void NamesEverySpanVectorByMethodAndHostOnly()
    {
        foreach (var vector in Vectors("span_name").EnumerateArray())
        {
            Assert.Equal(vector.GetProperty("strict").GetString(), StrictPrivacy.SpanName(vector.GetProperty("name").GetString()!));
        }
    }

    [Fact]
    public void IsOffByDefaultAndReadFromTheEnvironment()
    {
        Assert.Equal(Configuration.ResolveStrictPrivacy(System.Environment.GetEnvironmentVariable), new Configuration().StrictPrivacy);
        Assert.False(Configuration.ResolveStrictPrivacy(_ => null));
        Assert.True(Configuration.ResolveStrictPrivacy(name => name == "FORGE_OPS_STRICT_PRIVACY" ? "true" : null));
        Assert.True(Configuration.ResolveStrictPrivacy(name => name == "FORGE_OPS_STRICT_PRIVACY" ? "TRUE" : null));
        Assert.False(Configuration.ResolveStrictPrivacy(name => name == "FORGE_OPS_STRICT_PRIVACY" ? "false" : null));
        Assert.Empty(Configuration.ResolvePrivacyAllowedFields(_ => null));
        Assert.Equal(
            new HashSet<string> { "error_code", "feature" },
            Configuration.ResolvePrivacyAllowedFields(name => name == "FORGE_OPS_PRIVACY_ALLOWED_FIELDS" ? "error_code, feature,," : null));
    }

    [Fact]
    public void AnExplicitOptionWinsOverTheEnvironment()
    {
        var configuration = new Configuration { StrictPrivacy = true, PrivacyAllowedFields = new HashSet<string> { "error_code" } };
        Assert.True(configuration.StrictPrivacy);
        Assert.Equal(new HashSet<string> { "error_code" }, configuration.PrivacyAllowedFields);
    }

    [Fact]
    public void AStrictEventCarriesNoFreeText()
    {
        var configuration = NewConfiguration();
        configuration.StrictPrivacy = true;
        configuration.PrivacyAllowedFields = new HashSet<string> { "error_code", "retries", "flagged", "nested" };

        var (evt, reads) = Build(configuration);

        Assert.Equal("", evt.Message);
        Assert.Equal(StrictPrivacy.MessageFingerprint(ScrubbedMessage), evt.MessageFingerprint);
        Assert.Matches("^[0-9a-f]{32}$", evt.MessageFingerprint!);
        Assert.Equal(new Dictionary<string, object?> { ["error_code"] = "E42", ["retries"] = 3, ["flagged"] = true }, evt.Context);
        Assert.Null(evt.Tags);
        Assert.Equal(new Dictionary<string, object?> { ["id"] = "42" }, evt.User);
        Assert.Null(evt.Breadcrumbs);
        Assert.Null(evt.SqlStatement);
        Assert.Equal(0, reads);
        Assert.All(evt.Backtrace, frame =>
        {
            Assert.Null(frame.ContextLine);
            Assert.Null(frame.PreContext);
            Assert.Null(frame.PostContext);
        });

        // The structured fields stay exactly as they are without strict privacy.
        Assert.Equal("System.InvalidOperationException", evt.ExceptionClass);
        Assert.Equal("production", evt.Environment);
        Assert.Equal("1.2.3", evt.Release);
        Assert.Equal("dotnet", evt.SdkName);
        Assert.NotNull(evt.SqlObjects);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(evt)).RootElement;
        Assert.False(json.TryGetProperty("tags", out _));
        Assert.False(json.TryGetProperty("breadcrumbs", out _));
        Assert.False(json.TryGetProperty("sql_statement", out _));
        Assert.Equal(evt.MessageFingerprint, json.GetProperty("message_fingerprint").GetString());
        Assert.DoesNotContain("jane", json.GetRawText());
    }

    [Fact]
    public void AStrictEventLeavesOutContextAndUserWhenNothingIsAllowed()
    {
        var configuration = NewConfiguration();
        configuration.StrictPrivacy = true;

        var evt = new EventBuilder(configuration).Build(
            new Exception("x"),
            new Dictionary<string, object?> { ["error_code"] = "E42" },
            new Dictionary<string, object?> { ["email"] = "jane@example.com" });

        Assert.Null(evt.Context);
        Assert.Null(evt.Tags);
        Assert.Null(evt.User);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(evt)).RootElement;
        Assert.False(json.TryGetProperty("context", out _));
        Assert.False(json.TryGetProperty("user", out _));
    }

    [Fact]
    public void WithStrictPrivacyOffEventsAreExactlyAsBefore()
    {
        var (evt, reads) = Build(NewConfiguration());

        Assert.Equal(ScrubbedMessage, evt.Message);
        Assert.Null(evt.MessageFingerprint);
        Assert.Equal(5, evt.Context!.Count);
        Assert.Equal("free text", evt.Context["note"]);
        Assert.Empty(evt.Tags!);
        Assert.Equal(new Dictionary<string, object?> { ["id"] = 42, ["email"] = "jane@example.com", ["username"] = "jane" }, evt.User);
        Assert.Single(evt.Breadcrumbs!);
        Assert.Equal("SELECT * FROM patients WHERE id = ?", evt.SqlStatement);
        Assert.True(reads > 0);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(evt)).RootElement;
        Assert.Equal(
            new[] { "exception_class", "message", "backtrace", "occurred_at", "environment", "release", "server_name", "context", "tags", "sdk_name", "user", "breadcrumbs", "sql_objects", "sql_statement" },
            json.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void AStrictEventLeavesOutTheEndpointButKeepsTheTransactionName()
    {
        foreach (var strict in new[] { true, false })
        {
            var configuration = NewConfiguration();
            configuration.StrictPrivacy = strict;
            TrackedEvent? delivered = null;
            var queue = new Mock<IDeliveryQueue>();
            queue.Setup(q => q.Push(It.IsAny<TrackedEvent>())).Callback<TrackedEvent>(evt => delivered = evt).Returns(true);
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("patients/{id}"), 0, null, null));

            ForgeOpsTrackerSdk.CurrentRequest.Value = RequestState.For(context);
            try
            {
                new ForgeOpsReporter(configuration, new EventBuilder(configuration), queue.Object).Report(new Exception("x"));
            }
            finally
            {
                ForgeOpsTrackerSdk.CurrentRequest.Value = null;
            }

            Assert.Equal("GET patients/{id}", delivered!.TransactionName);
            Assert.Equal(strict ? null : "GET /patients/{id}", delivered.Endpoint);
        }
    }

    [Fact]
    public void ARawPathTransactionNameLosesItsIdsUnderStrictPrivacy()
    {
        foreach (var strict in new[] { true, false })
        {
            var configuration = NewConfiguration();
            configuration.StrictPrivacy = strict;
            TrackedEvent? delivered = null;
            var queue = new Mock<IDeliveryQueue>();
            queue.Setup(q => q.Push(It.IsAny<TrackedEvent>())).Callback<TrackedEvent>(evt => delivered = evt).Returns(true);
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/patients/42/notes";

            ForgeOpsTrackerSdk.CurrentRequest.Value = RequestState.For(context);
            try
            {
                new ForgeOpsReporter(configuration, new EventBuilder(configuration), queue.Object).Report(new Exception("x"));
            }
            finally
            {
                ForgeOpsTrackerSdk.CurrentRequest.Value = null;
            }

            Assert.Equal(strict ? "GET /patients/:path" : "GET /patients/42/notes", delivered!.TransactionName);
        }
    }

    [Fact]
    public void StrictPrivacyRecordsNoBreadcrumbs()
    {
        var configuration = NewConfiguration();
        configuration.StrictPrivacy = true;
        var buffer = new BreadcrumbBuffer(configuration);
        buffer.Add("looked up jane@example.com");
        Assert.Empty(buffer.All());

        configuration.StrictPrivacy = false;
        buffer.Add("looked up");
        Assert.Single(buffer.All());
    }

    [Fact]
    public void StrictSpansKeepOnlyMethodHostAndDatabaseData()
    {
        var configuration = NewConfiguration();
        configuration.StrictPrivacy = true;
        var buffer = new SpanBuffer(configuration);
        buffer.Record("a", buffer.RootSpanId, "GET https://api.example.com/patients/42?x=1", "http", DateTimeOffset.UtcNow, 5,
            new Dictionary<string, object?> { ["url"] = "https://api.example.com/patients/42", ["status"] = 200 });
        buffer.Record("b", buffer.RootSpanId, "Load patient", "database", DateTimeOffset.UtcNow, 5,
            new Dictionary<string, object?> { ["db.statement"] = "SELECT * FROM patients WHERE name = 'jane'", ["db.system"] = "postgresql", ["rows"] = 1 });

        var spans = buffer.Finish("GET /patients/42", DateTimeOffset.UtcNow, 5000)!.Spans;

        Assert.Equal("GET /patients/:path", spans[0].Name);
        Assert.Empty(spans[0].Data);
        Assert.Equal("GET api.example.com", spans[1].Name);
        Assert.Empty(spans[1].Data);
        Assert.Equal("Load patient", spans[2].Name);
        Assert.Equal(
            new Dictionary<string, object?> { ["db.statement"] = "SELECT * FROM patients WHERE name = ?", ["db.system"] = "postgresql" },
            spans[2].Data);
    }

    [Fact]
    public void SpansAreUnchangedWithStrictPrivacyOff()
    {
        var buffer = new SpanBuffer(NewConfiguration());
        buffer.Record("a", buffer.RootSpanId, "GET https://api.example.com/patients/42", "http", DateTimeOffset.UtcNow, 5,
            new Dictionary<string, object?> { ["status"] = 200 });

        var spans = buffer.Finish("GET /patients/42", DateTimeOffset.UtcNow, 5000)!.Spans;

        Assert.Equal("GET /patients/42", spans[0].Name);
        Assert.Equal("GET https://api.example.com/patients/42", spans[1].Name);
        Assert.Equal(new Dictionary<string, object?> { ["status"] = 200 }, spans[1].Data);
    }
}
