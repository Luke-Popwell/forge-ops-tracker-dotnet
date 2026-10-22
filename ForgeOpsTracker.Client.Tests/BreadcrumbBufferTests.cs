namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/breadcrumb_buffer_spec.rb.
public class BreadcrumbBufferTests
{
    private static Configuration NewConfiguration(int maxBreadcrumbs = 3) => new()
    {
        MaxBreadcrumbs = maxBreadcrumbs
    };

    [Fact]
    public void ReturnsEntriesInInsertionOrderEachCarryingTheGivenFieldsPlusATimestamp()
    {
        var buffer = new BreadcrumbBuffer(NewConfiguration());

        buffer.Add("User Load", "query", "info", new Dictionary<string, object?> { ["duration_ms"] = 1.2 });

        var entry = Assert.Single(buffer.All());
        Assert.Equal("query", entry.Category);
        Assert.Equal("User Load", entry.Message);
        Assert.Equal("info", entry.Level);
        Assert.Equal(1.2, entry.Data["duration_ms"]);
        Assert.True(DateTimeOffset.TryParse(entry.Timestamp, out _));
    }

    [Fact]
    public void DefaultsCategoryToCustomLevelToInfoAndDataToEmpty()
    {
        var buffer = new BreadcrumbBuffer(NewConfiguration());

        buffer.Add("checkpoint");

        var entry = Assert.Single(buffer.All());
        Assert.Equal("custom", entry.Category);
        Assert.Equal("info", entry.Level);
        Assert.Empty(entry.Data);
    }

    [Fact]
    public void DropsTheOldestEntryOnceMaxBreadcrumbsIsExceeded()
    {
        var buffer = new BreadcrumbBuffer(NewConfiguration(maxBreadcrumbs: 3));

        for (var i = 0; i < 4; i++)
        {
            buffer.Add($"entry {i}");
        }

        Assert.Equal(new[] { "entry 1", "entry 2", "entry 3" }, buffer.All().Select(e => e.Message));
    }

    [Fact]
    public void ReReadsMaxBreadcrumbsOnEveryAddNotJustAtConstruction()
    {
        var configuration = NewConfiguration(maxBreadcrumbs: 3);
        var buffer = new BreadcrumbBuffer(configuration);

        buffer.Add("one");
        buffer.Add("two");
        configuration.MaxBreadcrumbs = 1;
        buffer.Add("three");

        Assert.Equal(new[] { "three" }, buffer.All().Select(e => e.Message));
    }

    [Fact]
    public void AddsNothingAtAllWhenMaxBreadcrumbsIsZero()
    {
        var buffer = new BreadcrumbBuffer(NewConfiguration(maxBreadcrumbs: 0));

        buffer.Add("one");

        Assert.Empty(buffer.All());
    }

    [Fact]
    public void AllReturnsACopyNotTheInternalList()
    {
        var buffer = new BreadcrumbBuffer(NewConfiguration());
        buffer.Add("one");

        var snapshot = buffer.All();
        // All() returns an IReadOnlyList; the underlying instance mutating from a further Add
        // must not retroactively change a snapshot already taken.
        buffer.Add("two");

        Assert.Single(snapshot);
        Assert.Equal(2, buffer.All().Count);
    }

    [Fact]
    public void IsSafeUnderConcurrentAddsFromMultipleThreads()
    {
        // A single request's AsyncLocal buffer instance can be reached from several genuinely
        // concurrent branches of the same request (e.g. a Task.WhenAll of downstream calls); see
        // BreadcrumbBuffer's own doc comment for why the internal lock exists. Uses a high cap so
        // eviction doesn't make this a test of eviction instead of a test of "no entries lost or
        // corrupted, no exception, under real concurrent writers."
        var buffer = new BreadcrumbBuffer(NewConfiguration(maxBreadcrumbs: 10_000));
        const int perThread = 200;
        const int threadCount = 8;

        Parallel.For(0, threadCount, threadIndex =>
        {
            for (var i = 0; i < perThread; i++)
            {
                buffer.Add($"t{threadIndex}-{i}");
            }
        });

        Assert.Equal(threadCount * perThread, buffer.All().Count);
    }
}
