using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ForgeOpsTracker;

/// <summary>
/// Reads and writes the W3C Trace Context <c>traceparent</c> header
/// (https://www.w3.org/TR/trace-context/), the vendor-neutral format for carrying one trace across
/// service boundaries: <c>00-&lt;32 hex trace id&gt;-&lt;16 hex parent span id&gt;-&lt;2 hex flags&gt;</c>.
/// Mirrors gems/forge_ops_tracker's own TraceParent: <see cref="Parse"/> lets a request continue its
/// caller's trace, and <see cref="Build"/> is what TracingHttpMessageHandler sends to the next
/// service along.
///
/// Strict on the way in, the posture the spec asks receivers to take: a malformed value, uppercase
/// hex, the reserved version <c>ff</c>, or an all-zero trace or parent id all mean "no usable
/// header" (<see cref="Parse"/> returns null and a fresh trace starts), never half-trusted. A future
/// version is still accepted when its first four fields have version 00's shape; version 00 itself
/// must have exactly four.
/// </summary>
public static class TraceParent
{
    public const string Header = "traceparent";

    private static readonly Regex Pattern = new(
        "^([0-9a-f]{2})-([0-9a-f]{32})-([0-9a-f]{16})-([0-9a-f]{2})(-.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly string InvalidTraceId = new('0', 32);
    private static readonly string InvalidSpanId = new('0', 16);

    // Always "01" (sampled) on the way out: whether a trace is sent is only decided once it's over
    // (see Configuration.TraceCaptureThreshold), long after this header has gone out, so "this may be
    // recorded" is the only honest answer. The next service makes its own decision either way.
    private const string SampledFlags = "01";

    /// <summary>A usable incoming header's two ids: the caller's trace, and the caller's span this work runs under.</summary>
    public sealed record Context(string TraceId, string ParentSpanId);

    public static Context? Parse(string? value)
    {
        if (value is null) return null;

        var match = Pattern.Match(value.Trim());
        if (!match.Success) return null;

        var version = match.Groups[1].Value;
        var traceId = match.Groups[2].Value;
        var parentId = match.Groups[3].Value;
        if (version == "ff") return null;
        if (version == "00" && match.Groups[5].Success) return null;
        if (traceId == InvalidTraceId || parentId == InvalidSpanId) return null;

        return new Context(traceId, parentId);
    }

    public static string Build(string traceId, string spanId) => $"00-{traceId}-{spanId}-{SampledFlags}";

    /// <summary>32 lowercase hex characters, never all zeros (the spec's one invalid value).</summary>
    public static string GenerateTraceId() => RandomNonZeroHex(16);

    /// <summary>16 lowercase hex characters, never all zeros.</summary>
    public static string GenerateSpanId() => RandomNonZeroHex(8);

    private static string RandomNonZeroHex(int bytes)
    {
        byte[] raw;
        do
        {
            raw = RandomNumberGenerator.GetBytes(bytes);
        }
        while (raw.All(b => b == 0));

        return Convert.ToHexString(raw).ToLowerInvariant();
    }
}
