using System.Net;

namespace ForgeOpsTracker;

/// <summary>
/// How one delivery ended, as far as whoever holds the payload is concerned: gone out, never going
/// to go out, or worth another try. IClient still answers with a plain bool; this is what tells the
/// last two apart for the flushers that keep a batch between attempts.
/// </summary>
internal enum DeliveryResult
{
    /// <summary>Any 2xx.</summary>
    Delivered,

    /// <summary>
    /// A 4xx other than 408 and 429: the server will never take this payload (a bad key, a plan
    /// without the feature, a body it can't parse or that is too large), so sending it again only
    /// repeats the answer.
    /// </summary>
    Rejected,

    /// <summary>A network error, a timeout, a 5xx, a 408 or a 429: the same payload may well go through later.</summary>
    Failed
}

/// <summary>
/// The batch deliveries whose outcome a flusher needs in full. Internal, and separate from
/// IClient, because IClient is a public seam anyone may have implemented: its methods have to keep
/// returning bool. Client implements both; any other IClient is asked through its bool methods,
/// where false is a failure worth retrying (see <see cref="DeliveryResults"/>).
/// </summary>
internal interface IDeliveryResultClient
{
    Task<DeliveryResult> SendSessionCheckinAsync(SessionCheckin payload, CancellationToken cancellationToken);

    Task<DeliveryResult> SendPerformanceSamplesAsync(PerformanceSamplesBatch payload, CancellationToken cancellationToken);

    Task<DeliveryResult> SendMetricsAsync(CustomMetricsBatch payload, CancellationToken cancellationToken);

    Task<DeliveryResult> SendInfrastructureMetricsAsync(InfrastructureMetricsBatch payload, CancellationToken cancellationToken);
}

internal static class DeliveryResults
{
    /// <summary>
    /// How many flushes in a row may fail before a batch is dropped anyway, so an endpoint that
    /// stays down can't make every later flush carry (and a full buffer keep refusing) more and
    /// more.
    /// </summary>
    public const int MaxFailedFlushes = 5;

    public static DeliveryResult FromStatus(HttpStatusCode status)
    {
        var code = (int)status;
        if (code >= 200 && code < 300) return DeliveryResult.Delivered;
        if (code >= 400 && code < 500 && code != 408 && code != 429) return DeliveryResult.Rejected;
        return DeliveryResult.Failed;
    }

    public static DeliveryResult FromBool(bool delivered) => delivered ? DeliveryResult.Delivered : DeliveryResult.Failed;

    /// <summary>Why a batch that ended this way is let go, for the debug log.</summary>
    public static string DropReason(this DeliveryResult result) =>
        result == DeliveryResult.Rejected ? "the server rejected it" : $"delivery failed {MaxFailedFlushes} times in a row";

    public static async Task<DeliveryResult> SessionCheckinResultAsync(this IClient client, SessionCheckin payload, CancellationToken cancellationToken) =>
        client is IDeliveryResultClient results
            ? await results.SendSessionCheckinAsync(payload, cancellationToken).ConfigureAwait(false)
            : FromBool(await client.DeliverSessionCheckinAsync(payload, cancellationToken).ConfigureAwait(false));

    public static async Task<DeliveryResult> PerformanceSamplesResultAsync(this IClient client, PerformanceSamplesBatch payload, CancellationToken cancellationToken) =>
        client is IDeliveryResultClient results
            ? await results.SendPerformanceSamplesAsync(payload, cancellationToken).ConfigureAwait(false)
            : FromBool(await client.DeliverPerformanceSamplesAsync(payload, cancellationToken).ConfigureAwait(false));

    public static async Task<DeliveryResult> MetricsResultAsync(this IClient client, CustomMetricsBatch payload, CancellationToken cancellationToken) =>
        client is IDeliveryResultClient results
            ? await results.SendMetricsAsync(payload, cancellationToken).ConfigureAwait(false)
            : FromBool(await client.DeliverMetricsAsync(payload, cancellationToken).ConfigureAwait(false));

    public static async Task<DeliveryResult> InfrastructureMetricsResultAsync(this IClient client, InfrastructureMetricsBatch payload, CancellationToken cancellationToken) =>
        client is IDeliveryResultClient results
            ? await results.SendInfrastructureMetricsAsync(payload, cancellationToken).ConfigureAwait(false)
            : FromBool(await client.DeliverInfrastructureMetricsAsync(payload, cancellationToken).ConfigureAwait(false));
}
