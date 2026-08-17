namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkRunEventBufferTests
{
    private static readonly JsonSerializerOptions ProbeJsonOptions = new(JsonSerializerDefaults.Web);


    [Test]
    public void Replay_TrimsByEventCountBudgetAndResetsWhenAfterSequencePredatesRetainedWindow()
    {
        var buffer = new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions
        {
            MaxEventCount = 5
        }));
        var runId = Guid.NewGuid();
        for (var index = 0; index < 8; index++)
        {
            _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: $"chunk-{index}"));
        }

        var retained = buffer.Replay(runId, afterSequence: 3, runVersion: 1);
        AssertEx.False(retained.ResetRequired);
        AssertEx.Equal(expected: 5, retained.Events.Count);
        AssertEx.Equal(expected: 4L, retained.Events[0].Sequence);
        AssertEx.Equal(expected: 8L, retained.Events[^1].Sequence);
        for (var index = 1; index < retained.Events.Count; index++)
        {
            AssertEx.True(retained.Events[index].Sequence > retained.Events[index - 1].Sequence,
                "Replayed sequences must stay monotonic after count-budget trimming.");
        }

        var stale = buffer.Replay(runId, afterSequence: 0, runVersion: 1);
        AssertEx.True(stale.ResetRequired, "A cursor older than the count-trimmed retained window must force a reset.");
    }

    [Test]
    public void Replay_TrimsByUtf8ByteBudgetAndResetsWhenAfterSequencePredatesRetainedWindow()
    {
        // Size the budget from an actual serialized event so the test doesn't guess at JSON overhead: keep
        // room for exactly 3 of the 10 published events, with slack for the sequence number growing a digit.
        var runId = Guid.NewGuid();
        var payload = new string('a', 256);
        var probeBytes = JsonSerializer.SerializeToUtf8Bytes(new BenchmarkRunStreamEvent(runId, 1, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: payload)),
            ProbeJsonOptions).Length;
        var buffer = new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions
        {
            MaxUtf8Bytes = (probeBytes * 3) + 32
        }));
        for (var index = 0; index < 10; index++)
        {
            _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: payload));
        }

        var tail = buffer.Replay(runId, afterSequence: 7, runVersion: 1);
        AssertEx.False(tail.ResetRequired);
        AssertEx.Equal(expected: 3, tail.Events.Count);
        AssertEx.Equal(expected: 8L, tail.Events[0].Sequence);
        AssertEx.Equal(expected: 10L, tail.Events[^1].Sequence);
        for (var index = 1; index < tail.Events.Count; index++)
        {
            AssertEx.True(tail.Events[index].Sequence > tail.Events[index - 1].Sequence,
                "Replayed sequences must stay monotonic after byte-budget trimming.");
        }

        var stale = buffer.Replay(runId, afterSequence: 0, runVersion: 1);
        AssertEx.True(stale.ResetRequired, "A byte-budget trim must evict events old enough to force a reset for a stale cursor.");
    }

    [Test]
    public void Hub_EventsNeverContainSensitiveSnapshotOrRawErrors()
    {
        var actual = typeof(BenchmarkRunStreamPayload).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                      .Select(static property => property.Name)
                                                      .ToHashSet(StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(BenchmarkRunStreamPayload.Content),
            nameof(BenchmarkRunStreamPayload.State),
            nameof(BenchmarkRunStreamPayload.ToolCallId),
            nameof(BenchmarkRunStreamPayload.ToolName),
            nameof(BenchmarkRunStreamPayload.Arguments),
            nameof(BenchmarkRunStreamPayload.Result),
            nameof(BenchmarkRunStreamPayload.IsError),
            nameof(BenchmarkRunStreamPayload.EffectiveContextTokens),
            nameof(BenchmarkRunStreamPayload.DurationMs),
            nameof(BenchmarkRunStreamPayload.TotalTokens),
            nameof(BenchmarkRunStreamPayload.TokensPerSecond),
            nameof(BenchmarkRunStreamPayload.RunVersion),
            // Reviewed: the throughput split is six timing scalars the runtime measured about ITS OWN work — token
            // counts, milliseconds and derived rates. None of them is derived from the prompt, the output text, the
            // runtime snapshot or an error message, so none can carry content or an unredacted failure onto the hub.
            nameof(BenchmarkRunStreamPayload.TtftMs),
            nameof(BenchmarkRunStreamPayload.PromptTokens),
            nameof(BenchmarkRunStreamPayload.PromptTokensPerSecond),
            nameof(BenchmarkRunStreamPayload.GenerationTokens),
            nameof(BenchmarkRunStreamPayload.GenerationTokensPerSecond),
            nameof(BenchmarkRunStreamPayload.CachedPromptTokens)
        };

        AssertEx.True(actual.SetEquals(expected),
            $"BenchmarkRunStreamPayload must expose only reviewed scalar fields. Unexpected: [{string.Join(", ", actual.Except(expected))}], " +
            $"missing: [{string.Join(", ", expected.Except(actual))}]. A new field must be reviewed to ensure it cannot leak a raw snapshot or unredacted error.");
    }
}
