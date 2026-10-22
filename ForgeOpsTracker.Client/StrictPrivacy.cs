using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeOpsTracker;

/// <summary>
/// The rules <see cref="Configuration.StrictPrivacy"/> applies, for customers who can't let
/// personal or health data leave their own servers: only structured facts about an error are
/// sent, never free text that could hold a name, an email or a diagnosis. Ported from the
/// server's own StrictPrivacy (see sdks/STRICT_PRIVACY.md), which applies the same rules again on
/// arrival when the project's own Strict privacy setting is on; strict_privacy_vectors.json in the
/// test project pins the results, so the two can't drift apart unnoticed.
///
/// "Word character" is ASCII [A-Za-z0-9_] only, and whitespace is ASCII only, spelled out as
/// classes rather than \w, \b, \d, \s or IgnoreCase: .NET's versions of all of those are
/// Unicode-aware, so a digit in another script or a non-breaking space would otherwise change the
/// result.
/// </summary>
public static class StrictPrivacy
{
    internal const int FingerprintLength = 32;

    private const string Word = "A-Za-z0-9_";
    private const string Space = @" \t\r\n\f\v";
    private const int MaxSegmentLength = 40;

    // A route template's parameter segment: ":id", "{id}", "<int:id>", "[slug]" or "*path".
    private static readonly Regex RouteParameter = new(@"\A(?::[^\n]+|\{[^\n]+\}|<[^\n]+>|\[[^\n]+\]|\*[^\n]*)\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Email = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // A quoted value (KeyError: 'Jane Doe') is data, not the shape of the error. Single quotes only open
    // and close at a word boundary, so a contraction (Couldn't) or the `email' style never starts one.
    private static readonly Regex DoubleQuoted = new("\"[^\"\r\n]*\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SingleQuoted = new($"(?<![{Word}])'[^'\r\n]*'(?![{Word}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexObjectId = new("0x[0-9a-fA-F]{4,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Uuid = new(
        $"(?<![{Word}])[0-9a-fA-F]{{8}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{12}}(?![{Word}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Number = new($"(?<![{Word}])[0-9]+(?![{Word}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HttpMethod = new($"^([A-Za-z]+)[{Space}]+([^{Space}]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
    private static readonly Regex Scheme = new("^[A-Za-z][A-Za-z0-9+.-]*://", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Port = new(@":[0-9]+\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Span data that's structure, not content: the masked statement and which database it ran on.
    private static readonly string[] SpanDataKeys = { SpanBuffer.DbStatement, SpanBuffer.DbSystem };

    /// <summary>
    /// The message with emails, quoted values, hex object ids, UUIDs and standalone numbers replaced, in
    /// that order; null is "".
    /// </summary>
    public static string NormalizeMessage(string? message)
    {
        var normalized = Email.Replace(message ?? string.Empty, "<email>");
        normalized = DoubleQuoted.Replace(normalized, "<str>");
        normalized = SingleQuoted.Replace(normalized, "<str>");
        normalized = HexObjectId.Replace(normalized, "0x");
        normalized = Uuid.Replace(normalized, "<uuid>");
        return Number.Replace(normalized, "<n>");
    }

    /// <summary>
    /// What's sent in place of the message: the first 32 lowercase hex characters of the SHA-256 of
    /// the normalized message's UTF-8 bytes. Lets ForgeOps group errors without ever seeing the text.
    /// </summary>
    public static string MessageFingerprint(string? message)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeMessage(message)));
        return Convert.ToHexString(digest).ToLowerInvariant()[..FingerprintLength];
    }

    /// <summary>
    /// A path that can't name a person, with no query or fragment. A route template (any segment a
    /// parameter: ":id", "{id}", "&lt;int:id&gt;", "[slug]", "*path") is kept as it is. A raw path
    /// keeps only its first segment (":id" if it holds an ASCII digit or is longer than 40
    /// characters), then "/:path" if there was more: "/patients/jane-doe/12" is "/patients/:path".
    /// No segments at all is "/".
    /// </summary>
    public static string StrictPath(string? path)
    {
        var withoutQuery = (path ?? string.Empty).Split('?', '#')[0];
        var all = withoutQuery.Split('/');
        if (all.Any(RouteParameter.IsMatch)) return withoutQuery;

        var segments = all.Where(segment => segment.Length > 0).ToList();
        if (segments.Count == 0) return "/";

        var first = segments[0];
        var id = first.Any(c => c is >= '0' and <= '9') || CodePoints(first) > MaxSegmentLength;
        return (withoutQuery.StartsWith('/') ? "/" : "") + (id ? ":id" : first) + (segments.Count > 1 ? "/:path" : "");
    }

    /// <summary>Renamed <see cref="StrictPath"/>, which it now calls; kept so existing callers still compile.</summary>
    [Obsolete("Renamed StrictPath.")]
    public static string PathWithoutIds(string? path) => StrictPath(path);

    /// <summary>
    /// A span name (or a transaction name) with no path or host detail that could name a person:
    /// "GET https://api.example.com/patients/42?x=1" is "GET api.example.com", and a path, bare
    /// ("/patients/42") or after a method ("GET /patients/42"), has its ids taken out. A target that
    /// isn't a host keeps its path with ids taken out; anything else ("OrdersController#create") is
    /// returned unchanged. Read by hand, never with a URL parser, so an address a parser would
    /// reject can't make the whole address go out.
    /// </summary>
    public static string SpanName(string name)
    {
        if (name.StartsWith('/')) return StrictPath(name);

        var match = HttpMethod.Match(name);
        if (!match.Success) return name;

        var method = match.Groups[1].Value.ToUpperInvariant();
        if (!HttpMethods.Contains(method)) return name;

        var target = match.Groups[2].Value;
        if (target.StartsWith('/')) return $"{method} {StrictPath(target)}";

        return $"{method} {HostOf(target) ?? StrictPath(target)}";
    }

    /// <summary>Only the allowed top-level keys with a string, number or boolean value; null when nothing is left.</summary>
    internal static Dictionary<string, object?>? Allowed(IDictionary<string, object?>? fields, ICollection<string> allowedFields)
    {
        if (fields is null || allowedFields.Count == 0) return null;

        var kept = fields
            .Where(entry => allowedFields.Contains(entry.Key) && IsPlainValue(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        return kept.Count == 0 ? null : kept;
    }

    /// <summary>The user's id only, as a string; null when there's no id.</summary>
    internal static Dictionary<string, object?>? User(IDictionary<string, object?>? user)
    {
        if (user is null || !user.TryGetValue("id", out var raw)) return null;

        var id = raw is IFormattable formattable ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : raw?.ToString();
        return string.IsNullOrWhiteSpace(id) ? null : new Dictionary<string, object?> { ["id"] = id };
    }

    /// <summary>Span data keeps only the (already masked) db.statement and db.system.</summary>
    internal static IDictionary<string, object?> SpanData(IDictionary<string, object?> data) =>
        SpanDataKeys.Where(data.ContainsKey).ToDictionary(key => key, key => data[key]);

    private static bool IsPlainValue(object? value) =>
        value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    // The host of an outbound call's target: without any scheme://, userinfo or :port, and only
    // when it really is one (the address had a scheme or a port, or the host has a dot or is
    // localhost); null otherwise.
    private static string? HostOf(string target)
    {
        var scheme = Scheme.Match(target);
        var rest = scheme.Success ? target[scheme.Length..] : target;
        var end = rest.IndexOfAny(new[] { '/', '?', '#' });
        var authority = end < 0 ? rest : rest[..end];
        authority = authority[(authority.LastIndexOf('@') + 1)..];
        var port = Port.Match(authority);
        var host = port.Success ? authority[..port.Index] : authority;
        if (host.Length == 0) return null;

        return scheme.Success || port.Success || host.Contains('.') || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? host
            : null;
    }

    private static int CodePoints(string value) => value.Count(c => !char.IsLowSurrogate(c));
}
