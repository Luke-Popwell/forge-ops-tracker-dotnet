using System.Net;
using Microsoft.Extensions.Logging;

namespace ForgeOpsTracker.Tests;

/// <summary>
/// Answers every request with whatever status it was last told to and keeps each body it was sent:
/// what the flusher tests use to show how a batch is treated after a 4xx or a 5xx, with the real
/// Client in between rather than a mocked IClient (which can only say true or false).
/// </summary>
internal sealed class StatusHandler : HttpMessageHandler
{
    public StatusHandler(HttpStatusCode status) => Status = status;

    public HttpStatusCode Status { get; set; }

    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(Status);
    }
}

/// <summary>Keeps every formatted message logged through Configuration.Logger.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
