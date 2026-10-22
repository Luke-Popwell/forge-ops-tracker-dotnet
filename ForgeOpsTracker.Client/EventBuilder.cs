using System.Diagnostics;
using System.Globalization;

namespace ForgeOpsTracker;

/// <summary>
/// A single injectable seam so ForgeOpsReporter is testable without a real
/// EventBuilder: mirrors how the Ruby gem's ErrorSubscriber spec passes
/// an instance_double(ForgeOpsTracker::EventBuilder) rather than a real
/// one.
/// </summary>
public interface IEventBuilder
{
    TrackedEvent Build(Exception exception, IDictionary<string, object?>? context = null, IDictionary<string, object?>? user = null, IReadOnlyList<BreadcrumbEntry>? breadcrumbs = null);
}

/// <summary>
/// Turns a raised exception into the payload shape the ingestion API
/// expects. Ported from
/// gems/forge_ops_tracker/lib/forge_ops_tracker/event_builder.rb:
/// backtrace parsing there is a simple regex over MRI backtrace lines;
/// here it's <see cref="StackTrace"/> with file info, which needs a
/// portable PDB deployed alongside the assembly to resolve file/line at
/// all (common for Debug builds; opt in for Release via
/// <c>&lt;DebugType&gt;portable&lt;/DebugType&gt;</c> if you want this in
/// production). Frames without file info still get a method name.
/// </summary>
public sealed class EventBuilder : IEventBuilder
{
    private const int MaxFrames = 500;

    // How many lines of source to grab on either side of the culprit line (see
    // AttachSourceContext), and the longest a single captured line is allowed to be before
    // getting truncated: guards against a single pathological minified/generated line
    // ballooning the payload. ForgeOps itself re-truncates on arrival too, the same "don't just
    // trust the SDK" posture MaxFrames already gets on the server side.
    private const int ContextLines = 5;
    private const int MaxContextLineLength = 500;

    // Identifies this client to the server's auto language-detection on the project the event
    // lands in (see Project#note_sdk_platform server-side); matches this repo's own sdks/dotnet
    // directory name, the same convention every other language's client follows.
    private const string SdkName = "dotnet";

    private readonly Configuration _configuration;
    private readonly Func<string, string[]> _readSourceLines;

    public EventBuilder(Configuration configuration) : this(configuration, File.ReadAllLines)
    {
    }

    /// <summary>
    /// Also takes the function used to read a source file's lines off disk: a seam so
    /// EventBuilderTests can prove zero disk reads happen at all when
    /// <see cref="Configuration.CaptureSourceContext"/> is disabled, without needing to mock a
    /// static BCL method (<see cref="File.ReadAllLines(string)"/> isn't mockable directly).
    /// </summary>
    public EventBuilder(Configuration configuration, Func<string, string[]> readSourceLines)
    {
        _configuration = configuration;
        _readSourceLines = readSourceLines;
    }

    public TrackedEvent Build(Exception exception, IDictionary<string, object?>? context = null, IDictionary<string, object?>? user = null, IReadOnlyList<BreadcrumbEntry>? breadcrumbs = null)
    {
        if (_configuration.StrictPrivacy) return BuildStrict(exception, context, user);

        var evt = new TrackedEvent
        {
            ExceptionClass = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message ?? string.Empty,
            Backtrace = BuildBacktrace(exception),
            OccurredAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Environment = _configuration.Environment,
            Release = _configuration.Release,
            ServerName = _configuration.ServerName,
            Context = context is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(context),
            Tags = new Dictionary<string, object?>(),
            SdkName = SdkName,
            User = user is null or { Count: 0 } ? null : new Dictionary<string, object?>(user),
            Breadcrumbs = breadcrumbs is null or { Count: 0 } ? null : breadcrumbs.ToList()
        };

        evt = AttachSql(evt, exception);

        return _configuration.ScrubPii ? Scrub(evt) : evt;
    }

    /// <summary>
    /// The same event under <see cref="Configuration.StrictPrivacy"/>: no message (only its
    /// fingerprint, taken from the message as it would otherwise have been sent, so scrubbed when
    /// scrubbing is on, matching what the server fingerprints from a client without strict
    /// privacy), no breadcrumbs, statement or source context, the user's id only, and context/tags
    /// reduced to the allowed keys, each left out entirely when nothing is left.
    /// </summary>
    private TrackedEvent BuildStrict(Exception exception, IDictionary<string, object?>? context, IDictionary<string, object?>? user)
    {
        var message = exception.Message ?? string.Empty;
        var evt = new TrackedEvent
        {
            ExceptionClass = exception.GetType().FullName ?? exception.GetType().Name,
            Message = string.Empty,
            MessageFingerprint = StrictPrivacy.MessageFingerprint(_configuration.ScrubPii ? PiiScrubber.ScrubString(message) : message),
            Backtrace = BuildBacktrace(exception),
            OccurredAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Environment = _configuration.Environment,
            Release = _configuration.Release,
            ServerName = _configuration.ServerName,
            Context = StrictPrivacy.Allowed(context, _configuration.PrivacyAllowedFields),
            Tags = StrictPrivacy.Allowed(new Dictionary<string, object?>(), _configuration.PrivacyAllowedFields),
            SdkName = SdkName,
            User = StrictPrivacy.User(user)
        };

        evt = AttachSql(evt, exception);

        return _configuration.ScrubPii ? Scrub(evt) : evt;
    }

    // See SqlStatement for what's read off the exception and how it's masked. The statement itself
    // only goes out when CaptureSqlStatement is on (and StrictPrivacy is off); the extracted names go out on their own
    // (CaptureSqlObjects) so an issue can still name the procedure or view involved.
    private TrackedEvent AttachSql(TrackedEvent evt, Exception exception)
    {
        if (!_configuration.CaptureSqlObjects && !_configuration.CaptureSqlStatement)
        {
            return evt;
        }

        var masked = ForgeOpsTracker.SqlStatement.MaskStatement(ForgeOpsTracker.SqlStatement.FindIn(exception));
        if (masked is null)
        {
            return evt;
        }

        var objects = ForgeOpsTracker.SqlStatement.Objects(masked);
        return evt with
        {
            SqlObjects = objects is not null && _configuration.CaptureSqlObjects ? objects : null,
            SqlStatement = _configuration.CaptureSqlStatement && !_configuration.StrictPrivacy ? masked : null
        };
    }

    // exception_class/occurred_at/environment/release/server_name/sdk_name/User are left alone
    // (and so are transaction_name/endpoint/trace_id, which ForgeOpsReporter adds after this runs):
    // structured fields this client or the host app sets deliberately, not free text an
    // exception or its context could accidentally spill sensitive data into. User specifically
    // is a deliberate exemption, not an oversight: PiiScrubber's own email pattern would
    // otherwise redact the exact thing this field exists to carry (the `with` expression below
    // never touches it, so it passes through untouched either way). Breadcrumbs is *not* exempt,
    // unlike User: a trail entry's message/data is exactly the kind of free text (a caller-built
    // message, a data bag copied from arbitrary request/query state) the scrubber exists to
    // catch, matching gems/forge_ops_tracker/lib/forge_ops_tracker/event_builder.rb's own
    // treatment of this field. Category/Level are left alone: a small, code-controlled vocabulary
    // ("controller"/"custom", "info"/"error", ...) rather than free text, the same reasoning
    // InApp (a bool) on BacktraceFrame already gets by simply not appearing in this `with` at all.
    private static TrackedEvent Scrub(TrackedEvent evt) => evt with
    {
        Message = PiiScrubber.ScrubString(evt.Message),
        SqlStatement = evt.SqlStatement is null ? null : PiiScrubber.ScrubString(evt.SqlStatement),
        Backtrace = evt.Backtrace.Select(frame => frame with
        {
            File = frame.File is null ? null : PiiScrubber.ScrubString(frame.File),
            Method = frame.Method is null ? null : PiiScrubber.ScrubString(frame.Method),
            ContextLine = frame.ContextLine is null ? null : PiiScrubber.ScrubString(frame.ContextLine),
            PreContext = frame.PreContext?.Select(PiiScrubber.ScrubString).ToList(),
            PostContext = frame.PostContext?.Select(PiiScrubber.ScrubString).ToList()
        }).ToList(),
        // Null only under strict privacy, when nothing allowed is left: kept null, so it's omitted.
        Context = evt.Context is null ? null : AsDictionary(PiiScrubber.Scrub(evt.Context)),
        Tags = evt.Tags is null ? null : AsDictionary(PiiScrubber.Scrub(evt.Tags)),
        Breadcrumbs = evt.Breadcrumbs?.Select(entry => entry with
        {
            Message = PiiScrubber.ScrubString(entry.Message),
            Data = AsDictionary(PiiScrubber.Scrub(entry.Data))
        }).ToList()
    };

    private static Dictionary<string, object?> AsDictionary(object? scrubbed) =>
        scrubbed as Dictionary<string, object?> ?? new Dictionary<string, object?>();

    private List<BacktraceFrame> BuildBacktrace(Exception exception)
    {
        var trace = new StackTrace(exception, fNeedFileInfo: true);

        return trace.GetFrames()
            .Take(MaxFrames)
            .Select(BuildFrame)
            .ToList();
    }

    private BacktraceFrame BuildFrame(StackFrame frame)
    {
        var method = frame.GetMethod();
        var file = frame.GetFileName();
        var line = frame.GetFileLineNumber();
        var inApp = IsInApp(method);

        var backtraceFrame = new BacktraceFrame
        {
            File = file,
            Line = line > 0 ? line : null,
            Method = method is null ? null : $"{method.DeclaringType?.FullName}.{method.Name}",
            InApp = inApp
        };

        return _configuration.CaptureSourceContext && !_configuration.StrictPrivacy && inApp
            ? AttachSourceContext(backtraceFrame, file, line)
            : backtraceFrame;
    }

    private bool IsInApp(System.Reflection.MethodBase? method) =>
        method?.DeclaringType?.Assembly is { } assembly && _configuration.AppAssemblies.Contains(assembly);

    /// <summary>
    /// Reads a few lines of source straight off disk around the culprit line, at exactly the
    /// path recorded on the frame. Only ever called for an in-app frame with
    /// <see cref="Configuration.CaptureSourceContext"/> on (see <see cref="BuildFrame"/>):
    /// never a third-party dependency, and never at all when the flag is off. Best-effort: any
    /// failure (the file doesn't exist, isn't readable, or the path doesn't resolve: the
    /// expected case for most production deployments of a compiled language, which ship only
    /// the compiled assembly, not the original .cs files) just leaves the frame without
    /// context, never a reason to fail building the whole event.
    /// </summary>
    private BacktraceFrame AttachSourceContext(BacktraceFrame frame, string? file, int line)
    {
        if (string.IsNullOrEmpty(file) || line <= 0) return frame;

        try
        {
            var lines = _readSourceLines(file);
            var index = line - 1;
            if (index < 0 || index >= lines.Length) return frame;

            var from = Math.Max(index - ContextLines, 0);
            var to = Math.Min(index + ContextLines, lines.Length - 1);

            return frame with
            {
                ContextLine = TruncateLine(lines[index]),
                PreContext = lines[from..index].Select(TruncateLine).ToList(),
                PostContext = lines[(index + 1)..(to + 1)].Select(TruncateLine).ToList()
            };
        }
        catch (IOException)
        {
            return frame;
        }
        catch (UnauthorizedAccessException)
        {
            return frame;
        }
    }

    private static string TruncateLine(string line) =>
        line.Length <= MaxContextLineLength ? line : line[..MaxContextLineLength] + "...";
}
