using System.Diagnostics; using System.Globalization; using System.Reflection;

namespace ForgeOpsTracker.Tests;
file static class LowLineFixture { public static Exception Raise() { try { throw new InvalidOperationException("boom"); } catch (InvalidOperationException e) { return e; } } }

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/event_builder_spec.rb.
// Backtrace frames come from a real thrown/caught exception rather than a
// hand-written fake backtrace string: .NET builds frames from
// System.Diagnostics.StackTrace, not text parsing, so there's no
// equivalent to the Ruby spec's raw backtrace-line fixtures.
public class EventBuilderTests
{
    // AppAssemblies deliberately doesn't include this test assembly by
    // default here (matching Configuration's own real default of "just
    // the entry assembly"): under the VSTest test host, the entry
    // assembly is the test host itself, not this project's compiled
    // assembly, so the in-app tests below configure it explicitly.
    private static Configuration NewConfiguration() => new()
    {
        Environment = "production",
        Release = "abc123",
        ServerName = "web-1"
    };

    private static Exception RaiseAndCapture()
    {
        try
        {
            throw new ArgumentException("boom");
        }
        catch (ArgumentException e)
        {
            return e;
        }
    }

    [Fact]
    public void BuildsAPayloadWithTheExceptionClassMessageAndConfiguredMetadata()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = RaiseAndCapture();

        var payload = builder.Build(error, new Dictionary<string, object?> { ["url"] = "https://example.com" });

        Assert.Equal("System.ArgumentException", payload.ExceptionClass);
        Assert.Equal("boom", payload.Message);
        Assert.Equal("production", payload.Environment);
        Assert.Equal("abc123", payload.Release);
        Assert.Equal("web-1", payload.ServerName);
        Assert.Equal("https://example.com", payload.Context!["url"]);
        Assert.True(DateTimeOffset.TryParse(payload.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
        Assert.Equal("dotnet", payload.SdkName);
    }

    [Fact]
    public void ParsesRealStackFramesWithFileLineAndMethod()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = RaiseAndCapture();

        var frames = builder.Build(error).Backtrace;

        Assert.NotEmpty(frames);
        var frame = frames[0];
        Assert.Contains(nameof(EventBuilderTests), frame.Method);
        Assert.Contains(nameof(RaiseAndCapture), frame.Method);
    }

    [Fact]
    public void MarksAFrameFromARegisteredAppAssemblyAsInApp()
    {
        var configuration = NewConfiguration();
        configuration.AppAssemblies = new HashSet<Assembly> { typeof(EventBuilderTests).Assembly };
        var builder = new EventBuilder(configuration);
        var error = RaiseAndCapture();

        var frames = builder.Build(error).Backtrace;

        // RaiseAndCapture lives in this very test assembly, explicitly
        // registered above.
        Assert.Contains(frames, f => f.InApp);
    }

    [Fact]
    public void MarksEveryFrameAsNotInAppWhenItsAssemblyIsNotRegistered()
    {
        var configuration = NewConfiguration(); // AppAssemblies left at its default (just the entry assembly)
        var builder = new EventBuilder(configuration);
        var error = RaiseAndCapture();

        var frames = builder.Build(error).Backtrace;

        Assert.All(frames, f => Assert.False(f.InApp));
    }

    [Fact]
    public void ReturnsAnEmptyBacktraceWhenTheExceptionWasNeverThrown()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = new ArgumentException("boom"); // constructed, never thrown: no stack trace to capture

        Assert.Empty(builder.Build(error).Backtrace);
    }

    [Fact]
    public void ScrubsLikelyPiiOutOfTheMessageAndContextByDefault()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = new ArgumentException("failed for user@example.com");

        var payload = builder.Build(error, new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["email"] = "ada@example.com", ["password"] = "hunter2" }
        });

        Assert.Equal("failed for [EMAIL FILTERED]", payload.Message);
        var user = Assert.IsType<Dictionary<string, object?>>(payload.Context!["user"]);
        Assert.Equal("[EMAIL FILTERED]", user["email"]);
        Assert.Equal("[FILTERED]", user["password"]);
    }

    [Fact]
    public void LeavesThePayloadUntouchedWhenScrubPiiIsDisabled()
    {
        var configuration = NewConfiguration();
        configuration.ScrubPii = false;
        var builder = new EventBuilder(configuration);
        var error = new ArgumentException("failed for user@example.com");

        var payload = builder.Build(error, new Dictionary<string, object?> { ["email"] = "ada@example.com" });

        Assert.Equal("failed for user@example.com", payload.Message);
        Assert.Equal("ada@example.com", payload.Context!["email"]);
    }

    [Fact]
    public void IncludesTheUserWhenGivenOneNeverScrubbedEvenThoughItsAnEmail()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = new ArgumentException("boom");

        var payload = builder.Build(error, null, new Dictionary<string, object?> { ["id"] = 42, ["email"] = "ada@example.com" });

        Assert.NotNull(payload.User);
        Assert.Equal(42, payload.User!["id"]);
        Assert.Equal("ada@example.com", payload.User!["email"]);
    }

    [Fact]
    public void LeavesUserNullWhenNoneWasGiven()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = new ArgumentException("boom");

        var payload = builder.Build(error);

        Assert.Null(payload.User);
    }

    [Fact]
    public void IncludesBreadcrumbsWhenGivenSome()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = new ArgumentException("boom");
        var breadcrumbs = new List<BreadcrumbEntry>
        {
            new() { Category = "controller", Message = "GET /users/1", Level = "info", Timestamp = "2026-01-01T00:00:00Z" }
        };

        var payload = builder.Build(error, null, null, breadcrumbs);

        var breadcrumb = Assert.Single(payload.Breadcrumbs!);
        Assert.Equal("controller", breadcrumb.Category);
        Assert.Equal("GET /users/1", breadcrumb.Message);
    }

    [Fact]
    public void LeavesBreadcrumbsNullWhenNoneWereGivenOrTheListWasEmpty()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = new ArgumentException("boom");

        Assert.Null(builder.Build(error).Breadcrumbs);
        Assert.Null(builder.Build(error, null, null, new List<BreadcrumbEntry>()).Breadcrumbs);
    }

    [Fact]
    public void ScrubsLikelyPiiOutOfABreadcrumbsMessageAndDataByDefault()
    {
        var builder = new EventBuilder(NewConfiguration());
        var error = new ArgumentException("boom");
        var breadcrumbs = new List<BreadcrumbEntry>
        {
            new()
            {
                Category = "custom",
                Message = "failed for user@example.com",
                Data = new Dictionary<string, object?> { ["password"] = "hunter2", ["order_id"] = 7 }
            }
        };

        var payload = builder.Build(error, null, null, breadcrumbs);

        var breadcrumb = Assert.Single(payload.Breadcrumbs!);
        Assert.Equal("failed for [EMAIL FILTERED]", breadcrumb.Message);
        Assert.Equal("[FILTERED]", breadcrumb.Data["password"]);
        Assert.Equal(7, breadcrumb.Data["order_id"]);
    }

    [Fact]
    public void LeavesBreadcrumbsUntouchedWhenScrubPiiIsDisabled()
    {
        var configuration = NewConfiguration();
        configuration.ScrubPii = false;
        var builder = new EventBuilder(configuration);
        var error = new ArgumentException("boom");
        var breadcrumbs = new List<BreadcrumbEntry>
        {
            new() { Category = "custom", Message = "failed for user@example.com" }
        };

        var payload = builder.Build(error, null, null, breadcrumbs);

        Assert.Equal("failed for user@example.com", payload.Breadcrumbs!.Single().Message);
    }

    // --- source context capture -----------------------------------------------------------

    private static Configuration NewInAppConfiguration()
    {
        var configuration = NewConfiguration();
        configuration.AppAssemblies = new HashSet<Assembly> { typeof(EventBuilderTests).Assembly };
        return configuration;
    }

    private static int LineOf(Exception error) => new StackTrace(error, fNeedFileInfo: true).GetFrames()![0].GetFileLineNumber();

    private static string WriteSourceFile(IEnumerable<string> lines)
    {
        var file = Path.GetTempFileName();
        File.WriteAllLines(file, lines);
        return file;
    }

    [Fact]
    public void AttachesContextLinePreContextAndPostContextAroundAnInAppFramesCulpritLineByDefault()
    {
        var error = RaiseAndCapture();
        var culpritLine = LineOf(error);
        var sourceFile = WriteSourceFile(Enumerable.Range(1, culpritLine + 20).Select(n => $"line {n}"));
        var builder = new EventBuilder(NewInAppConfiguration(), _ => File.ReadAllLines(sourceFile));

        var frame = builder.Build(error).Backtrace[0];

        Assert.Equal($"line {culpritLine}", frame.ContextLine);
        Assert.Equal(Enumerable.Range(culpritLine - 5, 5).Select(n => $"line {n}"), frame.PreContext);
        Assert.Equal(Enumerable.Range(culpritLine + 1, 5).Select(n => $"line {n}"), frame.PostContext);
    }

    [Fact]
    public void ClampsPostContextAtTheEndOfTheFileRatherThanFailing()
    {
        var error = RaiseAndCapture();
        var culpritLine = LineOf(error);
        // The temp file ends exactly on the culprit's own line: nothing comes after it.
        var sourceFile = WriteSourceFile(Enumerable.Range(1, culpritLine).Select(n => $"line {n}"));
        var builder = new EventBuilder(NewInAppConfiguration(), _ => File.ReadAllLines(sourceFile));

        var frame = builder.Build(error).Backtrace[0];

        Assert.Equal(Enumerable.Range(culpritLine - 5, 5).Select(n => $"line {n}"), frame.PreContext);
        Assert.Empty(frame.PostContext!);
    }

    [Fact]
    public void ClampsPreContextAtTheStartOfTheFileRatherThanFailing()
    {
        // LowLineFixture (top of this file) exists purely to put a real `throw` at a small, known
        // line number: StackFrame's file/line always comes from a genuine portable PDB, so there's
        // no way to hand-construct a frame reporting an arbitrary line the way sdks/java and
        // sdks/kotlin's own tests build a synthetic StackTraceElement directly.
        var error = LowLineFixture.Raise();
        var culpritLine = LineOf(error);
        Assert.True(culpritLine < 5, $"fixture's throw must stay near the top of its file for this test to be meaningful (was line {culpritLine})");
        var sourceFile = WriteSourceFile(Enumerable.Range(1, culpritLine + 20).Select(n => $"line {n}"));
        var builder = new EventBuilder(NewInAppConfiguration(), _ => File.ReadAllLines(sourceFile));

        var frame = builder.Build(error).Backtrace[0];

        // Fewer than CONTEXT_LINES (5) entries: proves the from-index clamped at 0 instead of
        // going negative, the same guarantee the mid-file and end-of-file tests exercise for the
        // other two edges.
        Assert.Equal(Enumerable.Range(1, culpritLine - 1).Select(n => $"line {n}"), frame.PreContext);
    }

    [Fact]
    public void TruncatesAnIndividualLineLongerThanMaxContextLineLength()
    {
        var error = RaiseAndCapture();
        var culpritLine = LineOf(error);
        var lines = Enumerable.Range(1, culpritLine - 1).Select(n => $"line {n}").Append(new string('x', 600));
        var sourceFile = WriteSourceFile(lines);
        var builder = new EventBuilder(NewInAppConfiguration(), _ => File.ReadAllLines(sourceFile));

        var frame = builder.Build(error).Backtrace[0];

        Assert.Equal(new string('x', 500) + "...", frame.ContextLine);
    }

    [Fact]
    public void NeverAttachesContextToAFrameThatIsNotInApp()
    {
        var error = RaiseAndCapture();
        var culpritLine = LineOf(error);
        var sourceFile = WriteSourceFile(Enumerable.Range(1, culpritLine + 20).Select(n => $"line {n}"));
        var configuration = NewConfiguration(); // AppAssemblies left at its default: not in-app
        var builder = new EventBuilder(configuration, _ => File.ReadAllLines(sourceFile));

        var frame = builder.Build(error).Backtrace[0];

        Assert.Null(frame.ContextLine);
        Assert.Null(frame.PreContext);
        Assert.Null(frame.PostContext);
    }

    [Fact]
    public void LeavesTheFrameUntouchedWithNoFileReadAttemptedWhenCaptureSourceContextIsDisabled()
    {
        var error = RaiseAndCapture();
        var culpritLine = LineOf(error);
        var sourceFile = WriteSourceFile(Enumerable.Range(1, culpritLine + 20).Select(n => $"line {n}"));
        var configuration = NewInAppConfiguration();
        configuration.CaptureSourceContext = false;
        var reads = 0;
        var builder = new EventBuilder(configuration, path =>
        {
            reads++;
            return File.ReadAllLines(path);
        });
        _ = sourceFile; // never actually read: see the reads assertion below

        var frame = builder.Build(error).Backtrace[0];

        Assert.Null(frame.ContextLine);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void LeavesTheFrameUntouchedWhenTheRecordedFileCannotBeRead()
    {
        var error = RaiseAndCapture();
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid()}.cs");
        var builder = new EventBuilder(NewInAppConfiguration(), _ => File.ReadAllLines(missingPath));

        var frame = builder.Build(error).Backtrace[0];

        Assert.Null(frame.ContextLine);
    }
}
