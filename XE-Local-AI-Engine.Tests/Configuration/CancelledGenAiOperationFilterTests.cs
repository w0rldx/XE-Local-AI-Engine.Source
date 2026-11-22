namespace XE_Local_AI_Engine.Tests.Configuration;

using Serilog.Events;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ConfigureServices.IsExpectedGenAiOperationOutcome" /> drops only MEAI's operation-exception event from its chat
///     telemetry hop when the exception is a cancellation or an operator eject; every other combination still logs.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CancelledGenAiOperationFilterTests
{
    private const string Source = "Microsoft.Extensions.AI.OpenTelemetryChatClient";
    private const string EventName = "gen_ai.client.operation.exception";

    [Test]
    public void ACancellation_OfTheOperationEvent_FromTheTelemetryHop_IsDropped()
    {
        AssertEx.True(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new OperationCanceledException(), Source, EventName)));
    }

    // TaskCanceledException derives from OperationCanceledException; an HTTP client cancellation surfaces as one.
    [Test]
    public void ATaskCancellation_IsDropped()
    {
        AssertEx.True(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new TaskCanceledException(), Source, EventName)));
    }

    // An operator eject is an outcome the invocation layer reports, not a provider fault: a forced eject fails the in-flight request, a vetoed load refuses the ensure.
    [Test]
    public void AnOperatorEject_OfTheOperationEvent_IsDropped()
    {
        AssertEx.True(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new LlamaServerModelEjectedException("ejected", new HttpRequestException("reset")), Source, EventName)));
        AssertEx.True(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new OperatorEjectedException("ejected"), Source, EventName)));
    }

    [Test]
    public void AnOperatorEject_FromAnotherSource_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new OperatorEjectedException("ejected"), "Microsoft.Extensions.AI.OpenTelemetryEmbeddingGenerator", EventName)));
    }

    [Test]
    public void AProviderFailure_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new HttpRequestException("refused"), Source, EventName)));
    }

    [Test]
    public void TheSameEvent_FromAnotherSource_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new OperationCanceledException(), "Microsoft.Extensions.AI.OpenTelemetryEmbeddingGenerator", EventName)));
    }

    [Test]
    public void AnotherEvent_FromTheTelemetryHop_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new OperationCanceledException(), Source, "some.other.event")));
    }

    [Test]
    public void AnEventWithoutAnEventId_StillLogs()
    {
        AssertEx.False(ConfigureServices.IsExpectedGenAiOperationOutcome(Event(new OperationCanceledException(), Source, eventName: null)));
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
