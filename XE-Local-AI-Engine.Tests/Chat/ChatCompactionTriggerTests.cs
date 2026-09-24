namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The automatic post-turn compaction trigger: the pump hook enqueues on a Completed turn with the window the runner
///     reported, and the worker compacts only when the projected next-turn history crosses the configured fraction.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ChatCompactionTriggerTests
{
    [Test]
    public void Hook_OnACompletedTurn_EnqueuesACompactJobWithTheTurnsWindow()
    {
        var dispatcher = Substitute.For<IConversationMaintenanceDispatcher>();
        var conversationId = Guid.NewGuid();

        ChatCompactionTriggerHook.Build(dispatcher, conversationId)(State(InvocationStatus.Completed), Terminal());

        dispatcher.Received(1).Dispatch(Arg.Is<ConversationMaintenanceJob>(job => job.ConversationId == conversationId
                                                                                  && job.Kind == ConversationMaintenanceKind.Compact
                                                                                  && job.ModelName == "served-model"
                                                                                  && job.ContextCapacityTokens == 32_768
                                                                                  && job.ReservedOutputTokens == 2_048));
    }

    [Test]
    [Arguments(InvocationStatus.Failed)]
    [Arguments(InvocationStatus.Cancelled)]
    [Arguments(InvocationStatus.Running)]
    public void Hook_OnATurnThatDidNotComplete_EnqueuesNothing(InvocationStatus status)
    {
        var dispatcher = Substitute.For<IConversationMaintenanceDispatcher>();

        ChatCompactionTriggerHook.Build(dispatcher, Guid.NewGuid())(State(status), Terminal());

        dispatcher.DidNotReceiveWithAnyArgs().Dispatch(default!);
    }

    [Test]
    public void Hook_WhenTheRunnerNeverReportedAWindow_EnqueuesNothing()
    {
        var dispatcher = Substitute.For<IConversationMaintenanceDispatcher>();
        var state = State(InvocationStatus.Completed);
        state.ContextCapacityTokens = null;

        ChatCompactionTriggerHook.Build(dispatcher, Guid.NewGuid())(state, Terminal());

        dispatcher.DidNotReceiveWithAnyArgs().Dispatch(default!);
    }

    [Test]
    public void Compose_WhenOneHookThrows_StillRunsTheOtherAndNeverThrowsIntoThePump()
    {
        var ran = new List<string>();
        var composed = ChatCompactionTriggerHook.Compose(NullLogger.Instance,
            (_, _) => throw new InvalidOperationException("memory hook failed"),
            null,
            (_, _) => ran.Add("compaction"));

        composed(State(InvocationStatus.Completed), Terminal());

        AssertEx.Equal(expected: 1, ran.Count, "A throwing memory hook must not stop the compaction hook.");
    }

    [Test]
    public void Clone_CarriesTheReportedWindow()
    {
        // The pump and the hook read the CLONE, so a field missing from Clone silently arrives as null.
        var clone = State(InvocationStatus.Completed).Clone();

        AssertEx.Equal<int?>(32_768, clone.ContextCapacityTokens);
        AssertEx.Equal<int?>(2_048, clone.ReservedOutputTokens);
    }

    [Test]
    public async Task Job_WhenTheProjectionIsWithinTheFraction_DoesNotCompact()
    {
        // Usable window 8,192 − 1,024 = 7,168; at 0.75 the threshold is 5,376.
        await using var harness = new ConversationMaintenanceHarness(projectedTokens: 5_376);

        await harness.Worker.ProcessJobAsync(ConversationMaintenanceHarness.Job(Guid.NewGuid()), CancellationToken.None);

        AssertEx.Equal(expected: 0, harness.Compactions.Count);
        AssertEx.Equal("local-model", harness.Estimator.LastModelName ?? string.Empty, "The projection uses the turn's model for its calibrated divisor.");
    }

    [Test]
    public async Task Job_WhenTheProjectionCrossesTheFraction_CompactsOnTheNodeDefaultModel()
    {
        await using var harness = new ConversationMaintenanceHarness(projectedTokens: 5_377);
        var conversationId = Guid.NewGuid();

        await harness.Worker.ProcessJobAsync(ConversationMaintenanceHarness.Job(conversationId), CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Compactions.Count);
        AssertEx.True(harness.Compactions.TryPeek(out var call));
        AssertEx.Equal(conversationId, call.ConversationId);
        AssertEx.Null(call.RequestedModel, "The fold runs on the node default local model, not the chat's.");
        await harness.Persistence.Received(1).GetConversationForTurnAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Job_AppliesTheModelsObservedCorrectionToTheThreshold()
    {
        // 5,000 is under the 5,376 threshold, but a model known to count 1.5× more tokens than estimated tightens it to 3,584.
        await using var uncorrected = new ConversationMaintenanceHarness(projectedTokens: 5_000);
        await using var corrected = new ConversationMaintenanceHarness(projectedTokens: 5_000, observedCorrection: 1.5);

        await uncorrected.Worker.ProcessJobAsync(ConversationMaintenanceHarness.Job(Guid.NewGuid()), CancellationToken.None);
        await corrected.Worker.ProcessJobAsync(ConversationMaintenanceHarness.Job(Guid.NewGuid()), CancellationToken.None);

        AssertEx.Equal(expected: 0, uncorrected.Compactions.Count);
        AssertEx.Equal(expected: 1, corrected.Compactions.Count);
    }

    [Test]
    public async Task Job_WhenAutoCompactIsDisabled_NeitherReadsNorCompacts()
    {
        await using var harness = new ConversationMaintenanceHarness(new ConversationCompactionOptions
        {
            AutoCompactEnabled = false
        });

        await harness.Worker.ProcessJobAsync(ConversationMaintenanceHarness.Job(Guid.NewGuid()), CancellationToken.None);

        AssertEx.Equal(expected: 0, harness.Compactions.Count);
        await harness.Persistence.DidNotReceiveWithAnyArgs().GetConversationForTurnAsync(Guid.Empty, CancellationToken.None);
    }

    [Test]
    public async Task Job_WhenTheConversationIsGone_DoesNotCompact()
    {
        await using var harness = new ConversationMaintenanceHarness();
        harness.Persistence.GetConversationForTurnAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<NodeChatConversationDto?>(null));

        await harness.Worker.ProcessJobAsync(ConversationMaintenanceHarness.Job(Guid.NewGuid()), CancellationToken.None);

        AssertEx.Equal(expected: 0, harness.Compactions.Count);
    }

    private static InvocationState State(InvocationStatus status) =>
        new()
        {
            Status = status,
            ModelUsed = "served-model",
            ContextCapacityTokens = 32_768,
            ReservedOutputTokens = 2_048
        };

    private static NodeChatPumpTerminalResult Terminal() =>
        new()
        {
            Persisted = new NodeChatPersistedMessageDto
            {
                MessageId = Guid.NewGuid(),
                ConversationId = Guid.NewGuid(),
                RequestId = null,
                Sequence = 1,
                Role = "assistant",
                Content = "answer",
                Reasoning = null,
                Status = NodeChatMessageStatusValues.Completed,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                Model = null,
                Error = null,
                MetadataJson = null
            },
            TerminalStatus = NodeChatMessageStatusValues.Completed,
            EventType = "assistant.completed"
        };
}
