using System.Globalization;

namespace ForgeOpsTracker;

/// <summary>
/// The two metric buffers behind one small API: <see cref="CaptureMetric"/> for a business event you
/// name yourself, <see cref="CaptureInfrastructureMetric"/> for a reading from one of your own hosts.
/// Registered as a DI singleton by AddForgeOpsTracker (inject it wherever you capture), and the same
/// class backs ForgeOpsTrackerSdk's static methods for a console app or worker with no DI. Nothing
/// here is automatic, so there is no TrackMetrics flag: both are simply a no-op when the client
/// isn't enabled for the current environment. IAsyncDisposable, so the host's normal singleton
/// disposal on shutdown flushes whatever is left.
/// </summary>
public sealed class MetricsReporter : IAsyncDisposable
{
    private readonly Configuration _configuration;
    private readonly MetricBuffer<CustomMetricEntry> _custom;
    private readonly MetricBuffer<InfrastructureMetricEntry> _infrastructure;

    public MetricsReporter(Configuration configuration, IClient client)
    {
        _configuration = configuration;
        _custom = new MetricBuffer<CustomMetricEntry>(
            configuration,
            () => configuration.MetricFlushInterval,
            (entries, ct) => client.MetricsResultAsync(new CustomMetricsBatch { Metrics = entries }, ct));
        _infrastructure = new MetricBuffer<InfrastructureMetricEntry>(
            configuration,
            () => configuration.InfrastructureMetricFlushInterval,
            (entries, ct) => client.InfrastructureMetricsResultAsync(new InfrastructureMetricsBatch { Metrics = entries }, ct));
    }

    /// <summary>
    /// Records a named business metric (a signup, a payment, anything you want to name). <paramref name="value"/>
    /// defaults to 1 so a bare counter-style call needs no argument; pass one for a real magnitude
    /// (<c>CaptureMetric("payment", 49)</c>); it may be negative (a refund). A NaN or infinite value is dropped.
    /// </summary>
    public void CaptureMetric(string name, double value = 1.0)
    {
        if (!_configuration.IsEnabled()) return;

        _custom.Record(new CustomMetricEntry
        {
            MetricName = name,
            Value = value,
            Environment = _configuration.Environment,
            Release = _configuration.Release,
            RecordedAt = Now()
        }, value);
    }

    /// <summary>
    /// Records one infrastructure reading (CPU, memory, disk, anything else a script of yours reads)
    /// from one of your own hosts. <paramref name="hostname"/> defaults to Configuration.ServerName, so a
    /// script running on the box it reports about needs no argument.
    /// </summary>
    public void CaptureInfrastructureMetric(string name, double value, string? hostname = null)
    {
        if (!_configuration.IsEnabled()) return;

        _infrastructure.Record(new InfrastructureMetricEntry
        {
            MetricName = name,
            Value = value,
            Hostname = hostname ?? _configuration.ServerName ?? "",
            RecordedAt = Now()
        }, value);
    }

    /// <summary>Delivers every buffered metric and infrastructure reading right now, instead of waiting for the next flush interval.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _custom.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _infrastructure.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _custom.DisposeAsync().ConfigureAwait(false);
        await _infrastructure.DisposeAsync().ConfigureAwait(false);
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
