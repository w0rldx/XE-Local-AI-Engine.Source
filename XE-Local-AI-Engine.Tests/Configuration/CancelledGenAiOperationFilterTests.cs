namespace XE_Local_AI_Engine.Tests.Configuration;

using Serilog.Events;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ConfigureServices.IsCancelledGenAiOperation" /> drops only MEAI's operation-exception event from its chat
///     telemetry hop when the exception is a cancellation; every other combination still logs.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CancelledGenAiOperationFilterTests
{
    private const string Source = "Microsoft.Extensions.AI.OpenTelemetryChatClient";
    private const string EventName = "gen_ai.client.operation.exception";

    [Test]
    public void ACancellation_OfTheOperationEvent_FromTheTelemetryHop_IsDropped()
    {
        AssertEx.True(ConfigureServices.IsCancelledGenAiOperation(Event(new OperationCanceledException(), Source, EventName)));
    }

    // TaskCanceledException derives from OperationCanceledException; an HTTP client cancellation surfaces as one.
    [Test]
    public void ATaskCancellation_IsDropped()
    {
        AssertEx.True(ConfigureServices.IsCancelledGenAiOperation(Event(new TaskCanceledException(), Source, EventName)));
    }

    [Test]
    public void AProviderFailure_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsCancelledGenAiOperation(Event(new HttpRequestException("refused"), Source, EventName)));
    }

    [Test]
    public void TheSameEvent_FromAnotherSource_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsCancelledGenAiOperation(Event(new OperationCanceledException(), "Microsoft.Extensions.AI.OpenTelemetryEmbeddingGenerator", EventName)));
    }

    [Test]
    public void AnotherEvent_FromTheTelemetryHop_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsCancelledGenAiOperation(Event(new OperationCanceledException(), Source, "some.other.event")));
    }

    [Test]
    public void AnEventWithoutAnEventId_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsCancelledGenAiOperation(Event(new OperationCanceledException(), Source, eventName: null)));
    }

    private static LogEvent Event(Exception exception, string source, string? eventName)
    {
        List<LogEventProperty> properties = [new("SourceContext", new ScalarValue(source))];
        if (eventName is not null)
        {
            properties.Add(new LogEventProperty("EventId", new StructureValue([new LogEventProperty("Name", new ScalarValue(eventName))])));
        }

        return new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Warning, exception, new MessageTemplate(eventName ?? "message", []), properties);
    }
}
