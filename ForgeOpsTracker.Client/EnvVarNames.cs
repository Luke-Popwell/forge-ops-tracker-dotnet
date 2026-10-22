using System.Collections;
using System.Text.RegularExpressions;

namespace ForgeOpsTracker;

/// <summary>
/// The environment variable names a change snapshot sends when Configuration.TrackEnvVarNames is
/// on. Names only, never values. Drops host-specific noise (the hostname, the shell, the working
/// directory, per-pod service discovery variables, this client's own FORGE_OPS_* settings) so a
/// fleet of identical hosts doesn't look like it changes every time one restarts. The one denylist
/// for this SDK.
/// </summary>
internal static class EnvVarNames
{
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal)
    {
        "HOSTNAME", "HOST", "HOME", "PATH", "PWD", "OLDPWD", "SHLVL", "_", "TERM", "USER", "LOGNAME",
        "SHELL", "LANG", "TMPDIR", "TZ", "PORT", "DYNO", "INVOCATION_ID", "JOURNAL_STREAM"
    };

    private static readonly string[] Prefixes = { "LC_", "SYSTEMD_", "MEMORY_PRESSURE_", "KUBERNETES_", "FORGE_OPS_" };

    // *_SERVICE_HOST, *_SERVICE_PORT* and *_PORT_*_TCP*: the variables Kubernetes and Docker links
    // inject for every service they can see.
    private static readonly Regex ServiceDiscovery = new(@"_SERVICE_HOST$|_SERVICE_PORT|_PORT_.*_TCP", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="name"/> is host noise; compared uppercase, since Windows names are case-insensitive.</summary>
    public static bool IsNoise(string name)
    {
        var upper = name.ToUpperInvariant();
        return Exact.Contains(upper)
            || Prefixes.Any(prefix => upper.StartsWith(prefix, StringComparison.Ordinal))
            || ServiceDiscovery.IsMatch(upper);
    }

    /// <summary>The names in <paramref name="environment"/> that survive the denylist, sorted and distinct.</summary>
    public static IReadOnlyList<string> Collect(IDictionary environment)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string name && name.Length > 0 && !IsNoise(name)) names.Add(name);
        }

        return names.ToList();
    }
}
