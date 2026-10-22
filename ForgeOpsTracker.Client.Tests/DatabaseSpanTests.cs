using System.Text.Json;

namespace ForgeOpsTracker.Tests;

public class DatabaseSpanTests
{
    private static SpanBuffer NewBuffer() => new(new Configuration
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        Environment = "production",
        EnabledEnvironments = { "production" },
        TraceCaptureThreshold = TimeSpan.FromMilliseconds(10)
    });

    // Runs work inside a trace on buffer, then returns the finished payload serialized as it goes on the wire.
    private static async Task<(TracePayload Payload, string Json)> Trace(Func<Task> work)
    {
        var buffer = NewBuffer();
        ForgeOpsTrackerSdk.CurrentTrace.Value = buffer;
        ForgeOpsTrackerSdk.CurrentSpanId.Value = buffer.RootSpanId;
        try
        {
            await work();
        }
        finally
        {
            ForgeOpsTrackerSdk.CurrentTrace.Value = null;
            ForgeOpsTrackerSdk.CurrentSpanId.Value = null;
        }
        var payload = buffer.Finish("GET /orders", DateTimeOffset.UtcNow, 1500)!;
        return (payload, JsonSerializer.Serialize(payload));
    }

    [Fact]
    public async Task ADatabaseSpanSendsItsStatementMaskedWithItsSystemLowercased()
    {
        var (payload, json) = await Trace(async () =>
        {
            var rows = await ForgeOpsTrackerSdk.DatabaseSpanAsync(
                "Load orders",
                "SELECT * FROM orders WHERE email = 'jane@example.com' AND total > 4200",
                () => Task.FromResult(new[] { "row" }),
                dbSystem: "PostgreSQL");
            Assert.Equal(new[] { "row" }, rows);
        });

        var span = payload.Spans.Single(s => s.Name == "Load orders");
        Assert.Equal("database", span.Kind);
        Assert.Equal("SELECT * FROM orders WHERE email = ? AND total > ?", span.Data["db.statement"]);
        Assert.Equal("postgresql", span.Data["db.system"]);
        Assert.DoesNotContain("jane@example.com", json);
        Assert.DoesNotContain("4200", json);
    }

    [Fact]
    public async Task EveryDatabaseSpanHelperMasksTheStatement()
    {
        var (payload, json) = await Trace(() =>
        {
            using (ForgeOpsTrackerSdk.StartDatabaseSpan("Scoped", "SELECT name FROM users WHERE id = 9911", "mysql"))
            {
                ForgeOpsTrackerSdk.RecordDatabaseSpan("Timed", "UPDATE carts SET token = 'abc123'", DateTimeOffset.UtcNow, 2);
            }
            Assert.Equal(5, ForgeOpsTrackerSdk.DatabaseSpan("Sync", "SELECT 77", () => 5));
            return Task.CompletedTask;
        });

        var byName = payload.Spans.ToDictionary(s => s.Name);
        Assert.Equal("SELECT name FROM users WHERE id = ?", byName["Scoped"].Data["db.statement"]);
        Assert.Equal("mysql", byName["Scoped"].Data["db.system"]);
        Assert.Equal("UPDATE carts SET token = ?", byName["Timed"].Data["db.statement"]);
        Assert.Equal(byName["Scoped"].SpanId, byName["Timed"].ParentSpanId);
        Assert.Equal("SELECT ?", byName["Sync"].Data["db.statement"]);
        Assert.False(byName["Sync"].Data.ContainsKey("db.system"));
        Assert.DoesNotContain("9911", json);
        Assert.DoesNotContain("abc123", json);
    }

    [Fact]
    public async Task AStatementPutInDataByHandIsMaskedAndTheCallersDictionaryIsUntouched()
    {
        var data = new Dictionary<string, object?> { ["db.statement"] = "DELETE FROM carts WHERE token = 'abc123'", ["rows"] = 3 };
        var (payload, json) = await Trace(() =>
        {
            ForgeOpsTrackerSdk.RecordSpan("Clear cart", "database", DateTimeOffset.UtcNow, 2, data);
            ForgeOpsTrackerSdk.RecordSpan("Not a string", "database", DateTimeOffset.UtcNow, 2, new Dictionary<string, object?> { ["db.statement"] = 42 });
            return Task.CompletedTask;
        });

        var byName = payload.Spans.ToDictionary(s => s.Name);
        Assert.Equal("DELETE FROM carts WHERE token = ?", byName["Clear cart"].Data["db.statement"]);
        Assert.Equal(3, byName["Clear cart"].Data["rows"]);
        Assert.False(byName["Not a string"].Data.ContainsKey("db.statement"));
        Assert.Equal("DELETE FROM carts WHERE token = 'abc123'", data["db.statement"]);
        Assert.DoesNotContain("abc123", json);
    }

    [Fact]
    public void ALongStatementIsTruncated()
    {
        var statement = "SELECT " + string.Concat(Enumerable.Repeat("column_name, ", 500)) + "id FROM orders";
        var masked = SpanBuffer.MaskDatabaseData(ForgeOpsTrackerSdk.DatabaseSpanData(statement, "sqlite"));
        var value = (string)masked["db.statement"]!;
        Assert.Equal(4003, value.Length);
        Assert.EndsWith("...", value);
    }

    [Fact]
    public void DoubleQuotedStringsAreMaskedOnlyForMysqlAndMariadb()
    {
        const string statement = "SELECT id FROM users WHERE token = \"abc123secret\"";
        Assert.Equal("SELECT id FROM users WHERE token = ?",
            SpanBuffer.MaskDatabaseData(ForgeOpsTrackerSdk.DatabaseSpanData(statement, "MySQL"))["db.statement"]);
        Assert.Equal("SELECT id FROM users WHERE token = ?",
            SpanBuffer.MaskDatabaseData(new Dictionary<string, object?> { ["db.statement"] = statement, ["db.system"] = "mariadb" })["db.statement"]);
        Assert.Equal(statement,
            SpanBuffer.MaskDatabaseData(ForgeOpsTrackerSdk.DatabaseSpanData(statement, "postgresql"))["db.statement"]);
    }

    [Fact]
    public void DatabaseSpanDataLeavesOutBlankValues()
    {
        Assert.Empty(ForgeOpsTrackerSdk.DatabaseSpanData("  ", null));
        var data = ForgeOpsTrackerSdk.DatabaseSpanData("SELECT 1", " SQLite ");
        Assert.Equal("SELECT 1", data["db.statement"]);
        Assert.Equal("sqlite", data["db.system"]);
    }

    [Fact]
    public void OutsideATraceADatabaseSpanJustRunsTheWork()
    {
        Assert.Equal(7, ForgeOpsTrackerSdk.DatabaseSpan("free", "SELECT 1", () => 7, "postgresql"));
    }
}
