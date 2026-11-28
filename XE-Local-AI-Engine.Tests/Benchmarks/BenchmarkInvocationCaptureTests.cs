namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Contracts.Enums;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The capture is what a run stores and what its verifiers grade. Driven through the real dispatcher, so a
///     reclassification reaches it exactly as the runner reports one: the answer replaced, the reasoning appended.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkInvocationCaptureTests
{
    private const int JudgeContextTokens = 16_384;

    [Test]
    public async Task Reclassified_TextMovedToTheReasoning_IsNotGradedAsTheAnswer()
    {
        var dispatcher = NewDispatcher();
        var invocationId = Guid.NewGuid();
        await using var lease = await dispatcher.ReportInvocationAssignedAsync(Package(invocationId));
        using var capture = NewCapture(dispatcher, invocationId);

        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "draft says banana");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "</think>apple");
        await dispatcher.ReportInvocationTextReclassifiedAsync(invocationId, "apple", "draft says banana");

        var answer = GradedAnswer(capture);
        AssertEx.False(Verify("banana", answer).Passed, "a word only the scratchpad held must not pass a verifier");
        AssertEx.True(Verify("apple", answer).Passed);
        AssertEx.Equal("reasoning:draft says banana|output:apple", Describe(capture), "the stored transcript keeps the moved text as reasoning");
    }

    [Test]
    public async Task Reclassified_FinalRound_KeepsEarlierRoundsAndToolEventsInPlace()
    {
        var dispatcher = NewDispatcher();
        var invocationId = Guid.NewGuid();
        await using var lease = await dispatcher.ReportInvocationAssignedAsync(Package(invocationId));
        using var capture = NewCapture(dispatcher, invocationId);

        await dispatcher.ReportInvocationThinkingChunkAsync(invocationId, "plan");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "Let me look. ");
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Requested));
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Completed));
        await dispatcher.ReportInvocationThinkingChunkAsync(invocationId, "weigh");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "scratch</think>The answer is 42.");
        await dispatcher.ReportInvocationTextReclassifiedAsync(invocationId, "Let me look. The answer is 42.", "\n\nscratch");

        AssertEx.Equal("reasoning:plan|output:Let me look. |tool_call:call-1|tool_result:call-1|reasoning:weigh\n\nscratch|output:The answer is 42.",
            Describe(capture));
        AssertEx.Equal("Let me look. The answer is 42.", GradedAnswer(capture));
    }

    [Test]
    public async Task Reclassified_LiveEvents_ReplayToTheCapturedParts()
    {
        var dispatcher = NewDispatcher();
        var invocationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var buffer = new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions()));
        await using var lease = await dispatcher.ReportInvocationAssignedAsync(Package(invocationId));
        using var capture = new BenchmarkInvocationCapture(runId, invocationId, dispatcher, buffer);

        await dispatcher.ReportInvocationThinkingChunkAsync(invocationId, "plan");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "Let me look. ");
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Requested));
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Completed));
        await dispatcher.ReportInvocationThinkingChunkAsync(invocationId, "weigh");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "scratch</think>The answer is 42.");
        await dispatcher.ReportInvocationTextReclassifiedAsync(invocationId, "Let me look. The answer is 42.", "\n\nscratch");

        var events = buffer.Replay(runId, afterSequence: 0, runVersion: 0).Events;
        AssertEx.Equal(Describe(capture), FoldLiveEvents(events), "a live viewer must see the capture, not the leaked text plus the answer again");
        AssertEx.Equal("Let me look. ", events.Single(static e => e.Kind == BenchmarkRunStreamEventKind.OutputTrimmed).Payload.Content);
    }

    [Test]
    public async Task UnreclassifiedTurn_WithToolEvents_IsCapturedUnchanged()
    {
        var dispatcher = NewDispatcher();
        var invocationId = Guid.NewGuid();
        await using var lease = await dispatcher.ReportInvocationAssignedAsync(Package(invocationId));
        using var capture = NewCapture(dispatcher, invocationId);

        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "Let me look. ");
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Requested));
        await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Completed));
        await dispatcher.ReportInvocationThinkingChunkAsync(invocationId, "weigh");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "The answer ");
        await dispatcher.ReportInvocationStreamChunkAsync(invocationId, "is 42.");
        await dispatcher.ReportInvocationCompletedAsync(invocationId);

        AssertEx.Equal("output:Let me look. |tool_call:call-1|tool_result:call-1|reasoning:weigh|output:The answer is 42.", Describe(capture));
        AssertEx.Equal(InvocationStatus.Completed, AssertEx.NotNull(capture.TerminalState).Status);
    }

    private static string GradedAnswer(BenchmarkInvocationCapture capture)
    {
        // The stored round trip and the judge projection, exactly as the run executor and the judge executor apply them.
        var stored = BenchmarkExecutionSerialization.SerializeParts(BenchmarkOutputParts.Coalesce(capture.Parts));
        return BenchmarkJudgeVerifiers.AnswerText(BenchmarkOutputParts.ForJudge(BenchmarkExecutionSerialization.DeserializeParts(stored), JudgeContextTokens));
    }

    private static string Describe(BenchmarkInvocationCapture capture) =>
        string.Join('|', BenchmarkOutputParts.Coalesce(capture.Parts).Select(static part => $"{part.Kind}:{part.Content ?? part.ToolCallId}"));

    /// <summary>What the live reducer (BenchmarkLiveStreamModels <c>applyBenchmarkEvent</c>) builds from the events, in <see cref="Describe" /> form.</summary>
    private static string FoldLiveEvents(IEnumerable<BenchmarkRunStreamEvent> events)
    {
        List<BenchmarkOutputPart> parts = [];
        foreach (var streamEvent in events)
        {
            var content = streamEvent.Payload.Content ?? string.Empty;
            switch (streamEvent.Kind)
            {
                case BenchmarkRunStreamEventKind.OutputDelta or BenchmarkRunStreamEventKind.ReasoningDelta:
                    var kind = streamEvent.Kind == BenchmarkRunStreamEventKind.OutputDelta ? BenchmarkOutputParts.OutputKind : BenchmarkOutputParts.ReasoningKind;
                    if (parts.Count > 0 && parts[^1].Kind == kind)
                    {
                        parts[^1] = parts[^1] with
                        {
                            Content = parts[^1].Content + content
                        };
                    }
                    else
                    {
                        parts.Add(new BenchmarkOutputPart(kind, Content: content));
                    }

                    break;
                case BenchmarkRunStreamEventKind.ToolCall:
                    parts.Add(new BenchmarkOutputPart(BenchmarkOutputParts.ToolCallKind, ToolCallId: streamEvent.Payload.ToolCallId));
                    break;
                case BenchmarkRunStreamEventKind.ToolResult:
                    parts.Add(new BenchmarkOutputPart(BenchmarkOutputParts.ToolResultKind, ToolCallId: streamEvent.Payload.ToolCallId));
                    break;
                case BenchmarkRunStreamEventKind.OutputTrimmed:
                    var remaining = content.Length;
                    List<BenchmarkOutputPart> trimmed = [];
                    foreach (var part in parts)
                    {
                        if (part.Kind != BenchmarkOutputParts.OutputKind)
                        {
                            trimmed.Add(part);
                            continue;
                        }

                        var text = part.Content ?? string.Empty;
                        var kept = Math.Min(text.Length, remaining);
                        remaining -= kept;
                        if (kept > 0)
                        {
                            trimmed.Add(part with
                            {
                                Content = text[..kept]
                            });
                        }
                    }

                    parts = trimmed;
                    break;
            }
        }

        return string.Join('|', parts.Select(static part => $"{part.Kind}:{part.Content ?? part.ToolCallId}"));
    }

    private static BenchmarkJudgeVerifierResultV1 Verify(string word, string answer) =>
        BenchmarkJudgeVerifiers.Verify(new BenchmarkJudgeRubricCriterionV1("c0", "Title", "Description", 10, BenchmarkJudgeCriterionKinds.Regex,
            $$"""{"pattern":"{{word}}","mustMatch":true}"""), answer);

    private static BenchmarkInvocationCapture NewCapture(WorkerEventDispatcher dispatcher, Guid invocationId) =>
        new(Guid.NewGuid(), invocationId, dispatcher, new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions())));

    private static WorkerEventDispatcher NewDispatcher() =>
        new(Substitute.For<IInvocationRunner>(), Substitute.For<IInvocationHistory>(), NullLogger<WorkerEventDispatcher>.Instance, TimeProvider.System);

    private static RuntimePackage Package(Guid invocationId) =>
        RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithConversationId(Guid.NewGuid()).Build();

    private static ToolCallLifecyclePayload Lifecycle(Guid invocationId, ToolCallLifecyclePhase phase) =>
        new()
        {
            InvocationId = invocationId,
            ToolCallId = "call-1",
            ToolName = "web_search",
            Phase = phase,
            Arguments = phase == ToolCallLifecyclePhase.Requested ? "{}" : null,
            Result = phase == ToolCallLifecyclePhase.Completed ? "results" : null
        };
}
