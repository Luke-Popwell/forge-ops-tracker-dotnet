using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// "What changed": <see cref="RecordChange"/> records one explicit change (a flag flip, a config
/// edit, a migration), and a snapshot of what this process sees is sent once at startup so ForgeOps
/// can work out the rest itself. Same shape as DeliveryQueue and SpanQueue: a bounded channel drained
/// by one background task, so the caller never waits on the network and a failure (including the 403
/// a plan without change tracking gets) never reaches it. Registered as a DI singleton by
/// AddForgeOpsTracker (inject it wherever you record), and the same class backs
/// ForgeOpsTrackerSdk.RecordChange for a console app or worker with no DI.
/// </summary>
public sealed class ChangeTracker : IAsyncDisposable
{
    public const int MaxTitleLength = 200;

    // Once per process, not per instance: a DI app and the ambient ForgeOpsTrackerSdk facade can
    // both exist in one process, and the server would otherwise diff two identical snapshots.
    private static readonly OnceFlag ProcessSnapshot = new();

    private readonly Configuration _configuration;
    private readonly IClient _client;
    private readonly OnceFlag _snapshotOnce;
    private readonly Channel<object> _channel;
    private readonly Task _worker;

    public ChangeTracker(Configuration configuration, IClient client)
        : this(configuration, client, ProcessSnapshot)
    {
    }

    // Tests pass their own flag so each one gets a fresh "first snapshot" regardless of what other
    // tests running in parallel have already sent.
    internal ChangeTracker(Configuration configuration, IClient client, OnceFlag snapshotOnce)
    {
        _configuration = configuration;
        _client = client;
        _snapshotOnce = snapshotOnce;
        _channel = Channel.CreateBounded<object>(new BoundedChannelOptions(Math.Max(1, configuration.QueueSize))
        {
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Records one change. <paramref name="kind"/> is one of <see cref="ChangeKind"/>'s values; anything
    /// else is sent as "other". <paramref name="title"/> is required (a blank one drops the call) and cut
    /// to 200 characters. <paramref name="environment"/> defaults to Configuration.Environment and
    /// <paramref name="occurredAt"/> to now; pass <paramref name="id"/> to make a retried call
    /// idempotent. Returns whether it was queued; never throws, and is a no-op when the client isn't
    /// enabled for this environment.
    /// </summary>
    public bool RecordChange(
        string kind,
        string title,
        IDictionary<string, object?>? details = null,
        string? environment = null,
        string? service = null,
        string? actor = null,
        string? url = null,
        string? id = null,
        DateTimeOffset? occurredAt = null)
    {
        try
        {
            if (!_configuration.IsEnabled()) return false;
            if (string.IsNullOrWhiteSpace(title))
            {
                _configuration.Logger?.LogDebug("[ForgeOpsTracker] RecordChange called without a title, dropping it");
                return false;
            }

            var change = new ChangeRecord
            {
                Kind = NormalizeKind(kind),
                Title = Truncate(title.Trim(), MaxTitleLength),
                Details = ScrubDetails(details),
                Environment = string.IsNullOrWhiteSpace(environment) ? _configuration.Environment : environment,
                Service = service,
                Actor = actor,
                Url = url,
                Id = id,
                OccurredAt = FormatTime(occurredAt ?? DateTimeOffset.UtcNow)
            };

            return Enqueue(change);
        }
        catch (Exception ex)
        {
            _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] RecordChange failed");
            return false;
        }
    }

    /// <summary>
    /// Queues the startup snapshot, at most once per process; it's built and sent on the background
    /// worker, so the caller (app startup) never waits on reading the environment or the network. A
    /// no-op when the client isn't enabled or Configuration.DetectChanges is off.
    /// </summary>
    internal bool SendStartupSnapshot()
    {
        try
        {
            if (!_configuration.DetectChanges || !_configuration.IsEnabled()) return false;
            if (!_snapshotOnce.TryClaim()) return false;

            return Enqueue(SnapshotRequest.Instance);
        }
        catch (Exception ex)
        {
            _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] change snapshot failed");
            return false;
        }
    }

    internal static ChangeSnapshot BuildSnapshot(Configuration configuration, IDictionary environment) => new()
    {
        Environment = configuration.Environment,
        State = new ChangeSnapshotState
        {
            Runtime = Runtime(),
            EnvVarNames = configuration.TrackEnvVarNames ? EnvVarNames.Collect(environment) : null
        }
    };

    internal static string NormalizeKind(string? kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant();
        return normalized is not null && ChangeKind.All.Contains(normalized) ? normalized : ChangeKind.Other;
    }

    // ".net 8.0.11": the same lowercase "name version" shape every other SDK reports.
    private static string? Runtime()
    {
        var description = RuntimeInformation.FrameworkDescription?.Trim();
        return string.IsNullOrEmpty(description) ? null : description.ToLowerInvariant();
    }

    private Dictionary<string, object?>? ScrubDetails(IDictionary<string, object?>? details)
    {
        if (details is null || details.Count == 0) return null;
        if (!_configuration.ScrubPii) return new Dictionary<string, object?>(details);

        return details.ToDictionary(kv => kv.Key, kv => PiiScrubber.Scrub(kv.Value, kv.Key));
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;

        // Never split a surrogate pair in half.
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut];
    }

    private static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private bool Enqueue(object item)
    {
        if (_channel.Writer.TryWrite(item)) return true;

        _configuration.Logger?.LogDebug("[ForgeOpsTracker] change queue full, dropping change");
        return false;
    }

    private async Task RunAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (item)
                {
                    case ChangeRecord change:
                        await _client.DeliverChangeAsync(change).ConfigureAwait(false);
                        break;
                    case SnapshotRequest:
                        var snapshot = BuildSnapshot(_configuration, System.Environment.GetEnvironmentVariables());
                        await _client.DeliverChangeSnapshotAsync(snapshot).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] change worker error");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        var completed = await Task.WhenAny(_worker, Task.Delay(_configuration.ShutdownTimeout)).ConfigureAwait(false);
        if (completed != _worker)
        {
            _configuration.Logger?.LogDebug("[ForgeOpsTracker] change worker did not drain within ShutdownTimeout, abandoning it");
        }
    }

    private sealed class SnapshotRequest
    {
        public static readonly SnapshotRequest Instance = new();
    }
}

/// <summary>A flag that can be claimed exactly once, across threads.</summary>
internal sealed class OnceFlag
{
    private int _claimed;

    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
}

/// <summary>
/// Sends the startup change snapshot as the host starts. StartAsync only queues it and returns, so
/// it never delays startup; the snapshot is built and delivered on ChangeTracker's own worker.
/// </summary>
internal sealed class ChangeSnapshotHostedService : IHostedService
{
    private readonly ChangeTracker _tracker;

    public ChangeSnapshotHostedService(ChangeTracker tracker) => _tracker = tracker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tracker.SendStartupSnapshot();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
