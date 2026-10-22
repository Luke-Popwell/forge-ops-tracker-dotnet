using System.Net;
using System.Net.Http.Headers;

namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/client_spec.rb:
// FakeHttpMessageHandler stands in for the mocked Net::HTTP there.
public class ClientTests
{
    private static Configuration NewConfiguration(string? dsn = "https://secret-key@tracker.example.com/api/v1/events") => new()
    {
        Dsn = dsn
    };

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request);
        }
    }

    private static Client BuildClient(Configuration configuration, Func<HttpRequestMessage, HttpResponseMessage> respond, out FakeHttpMessageHandler handler)
    {
        handler = new FakeHttpMessageHandler(respond);
        return new Client(configuration, new HttpClient(handler));
    }

    [Fact]
    public async Task ReturnsTrueOnASuccessfulResponse()
    {
        var client = BuildClient(NewConfiguration(), _ => new HttpResponseMessage(HttpStatusCode.Accepted), out _);

        Assert.True(await client.DeliverAsync(new TrackedEvent { ExceptionClass = "RuntimeError" }));
    }

    [Fact]
    public async Task ReturnsFalseOnANon2xxResponse()
    {
        var client = BuildClient(NewConfiguration(), _ => new HttpResponseMessage(HttpStatusCode.Unauthorized), out _);

        Assert.False(await client.DeliverAsync(new TrackedEvent { ExceptionClass = "RuntimeError" }));
    }

    [Fact]
    public async Task ReturnsFalseWithoutThrowingWhenTheRequestThrowsANetworkError()
    {
        var client = BuildClient(NewConfiguration(), _ => throw new HttpRequestException("getaddrinfo failed"), out _);

        var result = await Record.ExceptionAsync(async () => await client.DeliverAsync(new TrackedEvent { ExceptionClass = "RuntimeError" }));
        Assert.Null(result);
        Assert.False(await client.DeliverAsync(new TrackedEvent { ExceptionClass = "RuntimeError" }));
    }

    // The expected result is its name: DeliveryResult is internal, and a public test method can't
    // take an internal type as a parameter.
    [Theory]
    [InlineData(202, "Delivered")]
    [InlineData(400, "Rejected")]
    [InlineData(401, "Rejected")]
    [InlineData(403, "Rejected")]
    [InlineData(413, "Rejected")]
    [InlineData(422, "Rejected")]
    [InlineData(408, "Failed")]
    [InlineData(429, "Failed")]
    [InlineData(500, "Failed")]
    [InlineData(503, "Failed")]
    public async Task TellsARejectedBatchApartFromOneThatOnlyFailedForNow(int status, string expected)
    {
        var client = BuildClient(NewConfiguration(), _ => new HttpResponseMessage((HttpStatusCode)status), out _);

        var result = await client.PerformanceSamplesResultAsync(new PerformanceSamplesBatch(), CancellationToken.None);

        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public async Task ANetworkErrorIsAFailureForNowNotARejection()
    {
        var client = BuildClient(NewConfiguration(), _ => throw new HttpRequestException("getaddrinfo failed"), out _);

        var result = await client.MetricsResultAsync(new CustomMetricsBatch(), CancellationToken.None);

        Assert.Equal(DeliveryResult.Failed, result);
    }

    [Fact]
    public async Task ReturnsFalseWhenThereIsNoDsnConfigured()
    {
        var client = BuildClient(NewConfiguration(dsn: null), _ => new HttpResponseMessage(HttpStatusCode.Accepted), out _);

        Assert.False(await client.DeliverAsync(new TrackedEvent { ExceptionClass = "RuntimeError" }));
    }

    [Fact]
    public async Task SendsTheApiKeyAsABearerTokenAndThePayloadAsJson()
    {
        var client = BuildClient(NewConfiguration(), _ => new HttpResponseMessage(HttpStatusCode.Accepted), out var handler);

        await client.DeliverAsync(new TrackedEvent { ExceptionClass = "RuntimeError", Message = "boom" });

        Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret-key"), handler.LastRequest!.Headers.Authorization);
        Assert.Contains("\"exception_class\":\"RuntimeError\"", handler.LastRequestBody);
        Assert.Contains("\"message\":\"boom\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task DeliversPerformanceSamplesAsABatchToThePerformanceSamplesUri()
    {
        var client = BuildClient(NewConfiguration(), _ => new HttpResponseMessage(HttpStatusCode.Accepted), out var handler);

        var batch = new PerformanceSamplesBatch
        {
            Samples = new List<PerformanceSample>
            {
                new() { TransactionName = "GET /posts/{id}", Environment = "production", RequestCount = 2 }
            }
        };
        var result = await client.DeliverPerformanceSamplesAsync(batch);

        Assert.True(result);
        Assert.Equal("https://tracker.example.com/api/v1/performance_samples", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"transaction_name\":\"GET /posts/{id}\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task DeliverPerformanceSamplesReturnsFalseWhenThereIsNoDsnConfigured()
    {
        var client = BuildClient(NewConfiguration(dsn: null), _ => new HttpResponseMessage(HttpStatusCode.Accepted), out _);

        Assert.False(await client.DeliverPerformanceSamplesAsync(new PerformanceSamplesBatch()));
    }
}
