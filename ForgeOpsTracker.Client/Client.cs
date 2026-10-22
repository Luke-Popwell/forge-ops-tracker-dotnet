using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker;

/// <summary>
/// A single injectable seam so DeliveryQueue is testable without real
/// HTTP: mirrors how the Ruby gem's DeliveryQueue spec passes an
/// instance_double(ForgeOpsTracker::Client) rather than a real one.
/// </summary>
public interface IClient
{
    Task<bool> DeliverAsync(TrackedEvent payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// A default interface method, not an abstract one: adding session-checkin delivery to this
    /// public interface after the fact would otherwise be a breaking change for anyone who's
    /// already implemented IClient themselves (a custom test double, say): the safe default
    /// (declining delivery) means an existing implementer keeps compiling unchanged, and only
    /// the real Client below actually overrides this to do anything.
    /// </summary>
    Task<bool> DeliverSessionCheckinAsync(SessionCheckin payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// Also a default interface method, for the same backward-compatibility reason as
    /// DeliverSessionCheckinAsync above.
    /// </summary>
    Task<bool> DeliverPerformanceSamplesAsync(PerformanceSamplesBatch payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Also a default interface method, for the same backward-compatibility reason.</summary>
    Task<bool> DeliverMetricsAsync(CustomMetricsBatch payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Also a default interface method, for the same backward-compatibility reason.</summary>
    Task<bool> DeliverInfrastructureMetricsAsync(InfrastructureMetricsBatch payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Also a default interface method, for the same backward-compatibility reason.</summary>
    Task<bool> DeliverSpansAsync(TracePayload payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Also a default interface method, for the same backward-compatibility reason.</summary>
    Task<bool> DeliverChangeAsync(ChangeRecord payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Also a default interface method, for the same backward-compatibility reason.</summary>
    Task<bool> DeliverChangeSnapshotAsync(ChangeSnapshot payload, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

/// <summary>
/// Delivers one payload over HTTP. Every failure mode: DNS, connection,
/// timeout, TLS, a non-2xx response: is caught here and turned into a
/// `false` return rather than a thrown exception, since a broken or
/// unreachable tracker must never be able to break the host app. Ported
/// from gems/forge_ops_tracker/lib/forge_ops_tracker/client.rb.
/// </summary>
public sealed class Client : IClient, IDeliveryResultClient
{
    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;

    public Client(Configuration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _httpClient = httpClient;
    }

    public Task<bool> DeliverAsync(TrackedEvent payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.IngestionUri(), payload, cancellationToken);

    public Task<bool> DeliverSessionCheckinAsync(SessionCheckin payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.SessionCheckinsUri(), payload, cancellationToken);

    public Task<bool> DeliverPerformanceSamplesAsync(PerformanceSamplesBatch payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.PerformanceSamplesUri(), payload, cancellationToken);

    public Task<bool> DeliverMetricsAsync(CustomMetricsBatch payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.CustomMetricsUri(), payload, cancellationToken);

    public Task<bool> DeliverInfrastructureMetricsAsync(InfrastructureMetricsBatch payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.InfrastructureMetricsUri(), payload, cancellationToken);

    public Task<bool> DeliverSpansAsync(TracePayload payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.SpansUri(), payload, cancellationToken);

    public Task<bool> DeliverChangeAsync(ChangeRecord payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.ChangesUri(), payload, cancellationToken);

    public Task<bool> DeliverChangeSnapshotAsync(ChangeSnapshot payload, CancellationToken cancellationToken = default) =>
        PostAsync(_configuration.ChangeSnapshotsUri(), payload, cancellationToken);

    Task<DeliveryResult> IDeliveryResultClient.SendSessionCheckinAsync(SessionCheckin payload, CancellationToken cancellationToken) =>
        SendAsync(_configuration.SessionCheckinsUri(), payload, cancellationToken);

    Task<DeliveryResult> IDeliveryResultClient.SendPerformanceSamplesAsync(PerformanceSamplesBatch payload, CancellationToken cancellationToken) =>
        SendAsync(_configuration.PerformanceSamplesUri(), payload, cancellationToken);

    Task<DeliveryResult> IDeliveryResultClient.SendMetricsAsync(CustomMetricsBatch payload, CancellationToken cancellationToken) =>
        SendAsync(_configuration.CustomMetricsUri(), payload, cancellationToken);

    Task<DeliveryResult> IDeliveryResultClient.SendInfrastructureMetricsAsync(InfrastructureMetricsBatch payload, CancellationToken cancellationToken) =>
        SendAsync(_configuration.InfrastructureMetricsUri(), payload, cancellationToken);

    private async Task<bool> PostAsync<T>(Uri? uri, T payload, CancellationToken cancellationToken) =>
        await SendAsync(uri, payload, cancellationToken).ConfigureAwait(false) == DeliveryResult.Delivered;

    private async Task<DeliveryResult> SendAsync<T>(Uri? uri, T payload, CancellationToken cancellationToken)
    {
        if (uri is null) return DeliveryResult.Failed;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.ApiKey);

            using var timeoutCts = new CancellationTokenSource(_configuration.Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            var result = DeliveryResults.FromStatus(response.StatusCode);
            if (result == DeliveryResult.Rejected)
            {
                _configuration.Logger?.LogDebug("[ForgeOpsTracker] delivery rejected: HTTP {StatusCode}", (int)response.StatusCode);
            }
            else if (result == DeliveryResult.Failed)
            {
                _configuration.Logger?.LogDebug("[ForgeOpsTracker] delivery failed: HTTP {StatusCode}", (int)response.StatusCode);
            }

            return result;
        }
        catch (Exception ex)
        {
            _configuration.Logger?.LogDebug(ex, "[ForgeOpsTracker] delivery failed");
            return DeliveryResult.Failed;
        }
    }
}
