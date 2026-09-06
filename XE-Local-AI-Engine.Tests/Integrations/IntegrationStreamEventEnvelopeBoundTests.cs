namespace XE_Local_AI_Engine.Tests.Integrations;

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The relation between <c>MaxOutputBytes</c> and <c>EventBufferMaxBytes</c>.
///     <para>
///         <c>MaxOutputBytes</c> bounds the PERSISTED <c>{"contentType": …, "payload": …}</c> envelope, but the replay
///         ring measures the whole serialized <see cref="IntegrationStreamEvent" /> wrapped around that envelope — the
///         type, the sequence, two GUIDs, a timestamp and their property names. Validating the two as equals therefore
///         accepted a configuration in which an exactly-maximal <c>external.output</c> trims the ring to empty the
///         moment it lands: the caller's stream had already sent 200, then closed with no output, and reattaching
///         answered 410.
///     </para>
/// </summary>
public sealed class IntegrationStreamEventEnvelopeBoundTests
{
    private static readonly JsonSerializerOptions RingOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void TheMaximalStreamEventOverheadFitsInsideThePinnedConstant()
    {
        // The measurement behind IntegrationOptions.MaxStreamEventEnvelopeBytes: the widest possible frame around a
        // payload, with the longest event type, the widest sequence and the widest timestamp. If this ever grows past
        // the constant the constant has to move, and this test is what says so.
        var payload = JsonDocument.Parse("""{"a":1}""").RootElement;
        var envelope = JsonSerializer.Serialize(new
            {
                contentType = "application/vnd.example.extremely-long-media-type+json",
                payload
            },
            RingOptions);

        var streamEvent = new IntegrationStreamEvent(IntegrationStreamEventTypes.ExecutionCancelled,
            long.MaxValue,
            Guid.NewGuid(),
            Guid.NewGuid(),
            long.MaxValue,
            "application/vnd.example.extremely-long-media-type+json",
            payload);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(streamEvent, RingOptions).Length;

        var overhead = serialized - Encoding.UTF8.GetByteCount(envelope);
        AssertEx.True(overhead > 0, $"A stream event is always wider than the envelope it carries; measured {overhead}.");
        AssertEx.True(overhead <= IntegrationOptions.MaxStreamEventEnvelopeBytes,
            $"The measured stream-event overhead is {overhead} bytes, past the pinned {IntegrationOptions.MaxStreamEventEnvelopeBytes}.");
    }

    [Test]
    public void AnExactlyMaximalOutputEventSurvivesARingSizedByTheValidatedRule()
    {
        // The boundary the old rule got wrong. The options are the smallest the validator now accepts for this
        // MaxOutputBytes, and the event is exactly at the output ceiling: it must still be readable afterwards.
        const int maxOutputBytes = 8_192;
        var options = new IntegrationOptions
        {
            MaxOutputBytes = maxOutputBytes,
            MaxOutputBytesPerExecution = maxOutputBytes,
            EventBufferMaxBytes = 65_536,
            EventBufferCapacity = 16
        };
        AssertEx.Empty(Validate(options), "The configuration under test has to be one the validator accepts.");

        using var buffer = new IntegrationExecutionEventBuffer(Options.Create(options), TimeProvider.System);
        var executionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        var payload = MaximalPayload(maxOutputBytes);
        var sequence = buffer.Reserve(executionId);
        buffer.Publish(new IntegrationStreamEvent(IntegrationStreamEventTypes.ExternalOutput,
            sequence,
            executionId,
            sessionId,
            OccurredAtUtc: 1,
            "application/json",
            payload));

        AssertEx.Equal(expected: 1L, buffer.Floor(executionId),
            "An exactly-maximal output must not trim the ring: a floor above 1 is the gap that hands the caller a 410 for output it already committed.");
        AssertEx.Equal(expected: 1L, buffer.LastSequence(executionId));
    }

    /// <summary>An <c>external.output</c> envelope of exactly <paramref name="maxOutputBytes" /> plaintext UTF-8 bytes.</summary>
    private static JsonElement MaximalPayload(int maxOutputBytes)
    {
        // Grown one character at a time would be O(n) serializations; the envelope's shape is fixed, so the filler
        // length follows from one measurement of the empty shape.
        var overhead = Encoding.UTF8.GetByteCount(Compose(string.Empty));
        var payload = Compose(new string('x', maxOutputBytes - overhead));
        AssertEx.Equal(maxOutputBytes, Encoding.UTF8.GetByteCount(payload), "The fixture must sit exactly on the ceiling, not near it.");
        return JsonDocument.Parse(payload).RootElement.GetProperty("payload").Clone();
    }

    private static string Compose(string filler) =>
        JsonSerializer.Serialize(new
            {
                contentType = "application/json",
                payload = new
                {
                    reading = filler
                }
            },
            RingOptions);

    private static IReadOnlyList<ValidationResult> Validate(IntegrationOptions options) =>
        [.. options.Validate(new ValidationContext(options))];
}
