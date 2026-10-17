namespace XE_Local_AI_Engine.Cli.Tests.Streaming;

using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ChatStreamWatchdogTests
{
    // Each row: the frames observed, written "type[:detail]", then the deadline in seconds the next read gets.
    [Test]
    [Arguments("nothing yet", "", 120)]
    [Arguments("streaming", "assistant-streaming", 180)]
    [Arguments("node ceiling raises the floor", "assistant-streaming:600", 630)]
    [Arguments("a short node ceiling never lowers the constants", "assistant-queued:10", 180)]
    [Arguments("cold load", "assistant-streaming,assistant-phase:loading_model", 660)]
    [Arguments("cold load ends at the first token", "assistant-phase:loading_model,assistant-delta:Hi", 180)]
    [Arguments("cold load ends at the generating phase", "assistant-phase:preparing_runtime,assistant-phase:generating", 180)]
    [Arguments("parked without a ceiling", "assistant-streaming,approval-requested", 660)]
    [Arguments("parked under the node ceiling", "assistant-streaming:900,question-requested", 930)]
    [Arguments("a resume snapshot keeps the park", "approval-requested,assistant-snapshot", 660)]
    [Arguments("a whitespace delta keeps the park", "approval-requested,assistant-delta: ", 660)]
    [Arguments("answer text ends the park", "approval-requested,assistant-delta:Done", 180)]
    [Arguments("a cold load ends the park", "approval-requested,assistant-phase:loading_model", 660)]
    public void NextDeadline_FollowsTheObservedFrames(string row, string frames, int expectedSeconds)
    {
        var watchdog = new ChatStreamWatchdog();
        foreach (var frame in frames.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            watchdog.Observe(ToFrame(frame));
        }

        AssertEx.Equal(TimeSpan.FromSeconds(expectedSeconds), watchdog.NextDeadline, row);
        AssertEx.Equal(frames.Length == 0, watchdog.AwaitingFirstFrame, row);
    }

    private static ChatStreamEvent ToFrame(string spec)
    {
        var separator = spec.IndexOf(':', StringComparison.Ordinal);
        var type = separator < 0 ? spec : spec[..separator];
        var detail = separator < 0 ? null : spec[(separator + 1)..];
        return new ChatStreamEvent
        {
            Type = type,
            RuntimePhase = type == ChatStreamEventTypes.AssistantPhase ? detail : null,
            Delta = type == ChatStreamEventTypes.AssistantDelta ? detail : null,
            InvocationTimeoutSeconds = type is ChatStreamEventTypes.AssistantStreaming or ChatStreamEventTypes.AssistantQueued && detail is not null
                ? int.Parse(detail, System.Globalization.CultureInfo.InvariantCulture)
                : null
        };
    }
}
