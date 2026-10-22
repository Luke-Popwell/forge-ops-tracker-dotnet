namespace ForgeOpsTracker.Tests;

/// <summary>
/// Exercises ForgeOpsTrackerSdk.AddBreadcrumb/RecordBreadcrumb and the CurrentBreadcrumbs
/// AsyncLocal directly (see ForgeOpsTracker.Client.csproj's own InternalsVisibleTo for why this
/// test project can reach them), for the properties that are far more directly proven by reading
/// the ambient state back in-process than by inferring them indirectly through an HTTP round
/// trip: standalone/lazy buffer creation with no middleware involved at all, and isolation across
/// genuinely divergent async branches. BreadcrumbContextMiddlewareTests covers the DI-registered,
/// TestServer-driven path built on top of this same facade.
///
/// CurrentBreadcrumbs/CurrentUser are a single, static, process-wide AsyncLocal each: a value set
/// directly on the test-runner's own thread (as opposed to inside a forked Task.Run, which
/// isolates by design; see below) has no framework-provided moment where it automatically resets
/// before the next test, unlike a fresh BreadcrumbContextMiddleware-scoped request. Every test
/// here resets both to null first, the same reason sdks/node's own suite needed an explicit
/// breadcrumbStorage.disable() in _resetForTesting for its own enterWith()-based lazy path (see
/// sdks/PROGRESS.md's Node.js entry): a plain assignment, unlike a scoped run()/using block, has
/// no natural unwind point of its own.
/// </summary>
public class ForgeOpsTrackerSdkTests
{
    public ForgeOpsTrackerSdkTests()
    {
        ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = null;
        ForgeOpsTrackerSdk.CurrentUser.Value = null;
    }

    [Fact]
    public void AddBreadcrumbLazilyCreatesABufferWhenNoneExistsYet()
    {
        Assert.Null(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value);

        ForgeOpsTrackerSdk.AddBreadcrumb("standalone checkpoint");

        var buffer = ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value;
        Assert.NotNull(buffer);
        var entry = Assert.Single(buffer!.All());
        Assert.Equal("standalone checkpoint", entry.Message);
        Assert.Equal("custom", entry.Category);
        Assert.Equal("info", entry.Level);
    }

    [Fact]
    public void AddBreadcrumbAddsToAnAlreadyExistingBufferRatherThanReplacingIt()
    {
        var existing = new BreadcrumbBuffer(ForgeOpsTrackerSdk.Configuration);
        existing.Add("first");
        ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = existing;

        ForgeOpsTrackerSdk.AddBreadcrumb("second");

        Assert.Same(existing, ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value);
        Assert.Equal(new[] { "first", "second" }, existing.All().Select(e => e.Message));
    }

    [Fact]
    public void AddBreadcrumbPassesCategoryLevelAndDataThrough()
    {
        var data = new Dictionary<string, object?> { ["order_id"] = 42 };

        ForgeOpsTrackerSdk.AddBreadcrumb("charged card", category: "billing", level: "warning", data: data);

        var entry = Assert.Single(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value!.All());
        Assert.Equal("billing", entry.Category);
        Assert.Equal("warning", entry.Level);
        Assert.Equal(42, entry.Data["order_id"]);
    }

    [Fact]
    public async Task AddBreadcrumbWorksAcrossAnAwaitTheSameWayItWouldInABackgroundTask()
    {
        // No HttpContext, no middleware: a plain async method standing in for a background
        // Task/worker service with no ASP.NET Core pipeline at all. Confirms the buffer created
        // before the first await is still the one AddBreadcrumb sees after it, the same
        // "AsyncLocal correctly flows through an await, unlike a ThreadLocal" property
        // ForgeOpsTrackerSdk.CurrentUser's own doc comment already establishes for SetUser.
        ForgeOpsTrackerSdk.AddBreadcrumb("before await");
        await Task.Delay(1);
        ForgeOpsTrackerSdk.AddBreadcrumb("after await");

        var messages = ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value!.All().Select(e => e.Message);
        Assert.Equal(new[] { "before await", "after await" }, messages);
    }

    [Fact]
    public async Task IsolatesTwoGenuinelyConcurrentDivergentAsyncBranchesFromEachOther()
    {
        // Task.Run captures (copies) the caller's current AsyncLocal state at the moment it
        // starts, but a mutation made inside one Task.Run body does not flow back out to the
        // caller or across to a sibling Task.Run: exactly the copy-on-write isolation this SDK's
        // per-request breadcrumb trail depends on (two concurrent, interleaved ASP.NET Core
        // requests are the identical shape, just started by the framework's own request dispatch
        // rather than by Task.Run directly). Delays are staggered so the two branches' own
        // continuations actually interleave rather than happening to run back-to-back.
        async Task<string[]> RunBranch(string label, int delayMs)
        {
            ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = new BreadcrumbBuffer(ForgeOpsTrackerSdk.Configuration);
            await Task.Delay(delayMs);
            ForgeOpsTrackerSdk.AddBreadcrumb($"{label}-breadcrumb");
            return ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value!.All().Select(e => e.Message).ToArray();
        }

        var branchA = Task.Run(() => RunBranch("a", 30));
        var branchB = Task.Run(() => RunBranch("b", 5));
        var results = await Task.WhenAll(branchA, branchB);

        Assert.Equal(new[] { "a-breadcrumb" }, results[0]);
        Assert.Equal(new[] { "b-breadcrumb" }, results[1]);
        // Neither branch's mutation escaped back to the thread that started them.
        Assert.Null(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value);
    }

    [Fact]
    public void RecordBreadcrumbDoesNothingWhenNoBufferExists()
    {
        Assert.Null(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value);

        // Internal; exercised here the same way PerformanceTrackingMiddleware calls it. A no-op,
        // not a lazily-created buffer: unlike AddBreadcrumb, RecordBreadcrumb is the automatic
        // source's own entry point, and BreadcrumbContextMiddleware not having set a buffer up
        // (TrackBreadcrumbs off, or no request at all) has to mean "record nothing," not "create
        // one anyway"; see RecordBreadcrumb's own doc comment.
        ForgeOpsTrackerSdk.RecordBreadcrumb("GET /users/1", "controller");

        Assert.Null(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value);
    }

    [Fact]
    public void RecordBreadcrumbAddsToAnExistingBuffer()
    {
        ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value = new BreadcrumbBuffer(ForgeOpsTrackerSdk.Configuration);

        ForgeOpsTrackerSdk.RecordBreadcrumb("GET /users/1", "controller", "error", new Dictionary<string, object?> { ["status"] = 500 });

        var entry = Assert.Single(ForgeOpsTrackerSdk.CurrentBreadcrumbs.Value!.All());
        Assert.Equal("controller", entry.Category);
        Assert.Equal("error", entry.Level);
        Assert.Equal(500, entry.Data["status"]);
    }
}
