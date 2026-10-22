using Moq;

namespace ForgeOpsTracker.Tests;

// Mirrors gems/forge_ops_tracker/spec/forge_ops_tracker/error_subscriber_spec.rb.
public class ForgeOpsReporterTests
{
    private static Configuration EnabledConfiguration() => new()
    {
        Dsn = "https://key@tracker.example.com/api/v1/events",
        EnabledEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "production" },
        Environment = "production"
    };

    private static (Mock<IEventBuilder> EventBuilder, Mock<IDeliveryQueue> DeliveryQueue) Mocks(TrackedEvent builtPayload)
    {
        var eventBuilder = new Mock<IEventBuilder>();
        eventBuilder.Setup(b => b.Build(
            It.IsAny<Exception>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IReadOnlyList<BreadcrumbEntry>?>())).Returns(builtPayload);

        var deliveryQueue = new Mock<IDeliveryQueue>();
        deliveryQueue.Setup(q => q.Push(It.IsAny<TrackedEvent>())).Returns(true);

        return (eventBuilder, deliveryQueue);
    }

    [Fact]
    public void BuildsAndEnqueuesAPayloadWhenReportingIsEnabled()
    {
        var configuration = EnabledConfiguration();
        var builtPayload = new TrackedEvent { ExceptionClass = "RuntimeError" };
        var (eventBuilder, deliveryQueue) = Mocks(builtPayload);
        var reporter = new ForgeOpsReporter(configuration, eventBuilder.Object, deliveryQueue.Object);
        var error = new InvalidOperationException("boom");
        var context = new Dictionary<string, object?> { ["a"] = 1 };

        reporter.Report(error, context);

        eventBuilder.Verify(b => b.Build(error, context, null, null), Times.Once);
        deliveryQueue.Verify(q => q.Push(builtPayload), Times.Once);
    }

    [Fact]
    public void PassesTheUserThroughToTheEventBuilder()
    {
        var configuration = EnabledConfiguration();
        var builtPayload = new TrackedEvent { ExceptionClass = "RuntimeError" };
        var (eventBuilder, deliveryQueue) = Mocks(builtPayload);
        var reporter = new ForgeOpsReporter(configuration, eventBuilder.Object, deliveryQueue.Object);
        var error = new InvalidOperationException("boom");
        var user = new Dictionary<string, object?> { ["id"] = 42, ["email"] = "alice@example.com" };

        reporter.Report(error, null, user);

        eventBuilder.Verify(b => b.Build(error, null, user, null), Times.Once);
    }

    [Fact]
    public void PassesTheBreadcrumbsThroughToTheEventBuilder()
    {
        var configuration = EnabledConfiguration();
        var builtPayload = new TrackedEvent { ExceptionClass = "RuntimeError" };
        var (eventBuilder, deliveryQueue) = Mocks(builtPayload);
        var reporter = new ForgeOpsReporter(configuration, eventBuilder.Object, deliveryQueue.Object);
        var error = new InvalidOperationException("boom");
        var breadcrumbs = new List<BreadcrumbEntry> { new() { Category = "custom", Message = "checkpoint" } };

        reporter.Report(error, null, null, breadcrumbs);

        eventBuilder.Verify(b => b.Build(error, null, null, breadcrumbs), Times.Once);
    }

    [Fact]
    public void DoesNothingWhenReportingIsDisabled()
    {
        var configuration = EnabledConfiguration();
        configuration.Environment = "development"; // not in EnabledEnvironments
        var (eventBuilder, deliveryQueue) = Mocks(new TrackedEvent());
        var reporter = new ForgeOpsReporter(configuration, eventBuilder.Object, deliveryQueue.Object);

        reporter.Report(new InvalidOperationException("boom"));

        deliveryQueue.Verify(q => q.Push(It.IsAny<TrackedEvent>()), Times.Never);
    }

    [Fact]
    public void NeverThrowsEvenIfBuildingTheEventFails()
    {
        var configuration = EnabledConfiguration();
        var eventBuilder = new Mock<IEventBuilder>();
        eventBuilder.Setup(b => b.Build(
            It.IsAny<Exception>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IReadOnlyList<BreadcrumbEntry>?>()))
            .Throws(new InvalidOperationException("event builder exploded"));
        var deliveryQueue = new Mock<IDeliveryQueue>();
        var reporter = new ForgeOpsReporter(configuration, eventBuilder.Object, deliveryQueue.Object);

        var exception = Record.Exception(() => reporter.Report(new InvalidOperationException("boom")));

        Assert.Null(exception);
    }

    [Fact]
    public void NeverThrowsEvenIfEnqueueingFails()
    {
        var configuration = EnabledConfiguration();
        var (eventBuilder, _) = Mocks(new TrackedEvent());
        var deliveryQueue = new Mock<IDeliveryQueue>();
        deliveryQueue.Setup(q => q.Push(It.IsAny<TrackedEvent>())).Throws(new InvalidOperationException("queue exploded"));
        var reporter = new ForgeOpsReporter(configuration, eventBuilder.Object, deliveryQueue.Object);

        var exception = Record.Exception(() => reporter.Report(new InvalidOperationException("boom")));

        Assert.Null(exception);
    }
}
