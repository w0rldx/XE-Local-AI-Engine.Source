namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static InvocationRunnerTestSupport;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

[Category(TestCategories.Unit)]
public sealed class InvocationRunnerAnswerTests
{
    [Test]
    public async Task RunAsync_WhenAToolReturnsTheDisabledMarker_SurfacesAToolDisabledNoticeOncePerTool()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ToolDisabledUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.ToolDisabled
            && payload.Detail == "test-tool"
            && payload.Message.Contains("test-tool", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_WhenAToolReturnsAnImage_ReportsTheImageBesideThePlaceholderText()
    {
        var image = new DataContent(new byte[]
        {
            1,
            2,
            3
        }, "image/png");
        var projected = AssertEx.NotNull(McpToolResultProjectionAIFunction.Project(image, "test-tool") as string);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ToolRoundUpdates(projected, finalText: "done"));
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Completed
            && payload.Result == projected
            && !payload.IsError
            && payload.Media.Count == 1
            && ReferenceEquals(payload.Media[0], image)));
    }

    [Test]
    public async Task RunAsync_WhenAToolReturnsATypedFailure_ReportsTheCallAsFailed()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: ToolRoundUpdates("[tool error: timeout] The MCP tool 'test-tool' did not respond.", finalText: "done"));

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build());

        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Completed && payload.IsError));
    }

    [Test]
    public async Task RunAsync_WhenTheRoundAfterAToolResultIsSilent_EndsWithAnEmptyAnswerNotice()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ToolRoundUpdates("ok", finalText: null));
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.EmptyAnswer
            && payload.Message == "The model stopped without an answer."));
    }

    /// <summary>
    ///     A turn that spent its whole room thinking must not read only "stopped without an answer", which makes the
    ///     user believe the reasoning was lost. It is persisted and shown, so the notice says so.
    /// </summary>
    /// <remarks>
    ///     "stop" is the reasoning budget: llama-server closes the block when the budget runs out and the model can end
    ///     there with no answer. The plain notice blamed nothing the user could act on.
    /// </remarks>
    [Test]
    [Arguments("length")]
    [Arguments("stop")]
    public async Task RunAsync_WhenTheTurnRanOutOfRoomWhileThinking_SaysTheThoughtsAreKept(string finishReason)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ThinkingThenFinish(finishReason));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EmptyAnswer
            && payload.Message == "The model stopped while thinking before it could answer; its thoughts are kept above."
            && payload.Detail == finishReason));
    }

    /// <summary>
    ///     After one Calculate call a 9B can write the next call as <c>&lt;tool_call&gt;</c> inside its reasoning,
    ///     ending the turn "stopped without an answer". The runner re-prompts exactly once, replaying the
    ///     executed call and its result.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenTheFinalRoundReasonsAToolCall_RepromptsOnceAndTheRetryAnswers()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var segments = new List<IReadOnlyList<ChatMessage>>();
        var thinkingOffRounds = new List<bool>();
        var calls = 0;
        var factory = CreateMessageCapturingFactory(_ =>
            {
                // What the provider-round hop (ProviderCallBudgetChatClient) reads as each segment starts and ends.
                thinkingOffRounds.Add(ProviderCallBudget.Current?.IsThinkingOff == true);
                return RecordThinkingOffAtEnd(Interlocked.Increment(ref calls) == 1 ? ToolRoundThenReasonedToolCall() : CreateUpdates("7105417"), thinkingOffRounds);
            },
            messages => segments.Add([.. messages]));
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build());

        AssertEx.Equal(2, segments.Count);
        var retry = segments[1];
        AssertEx.Equal(ChatRole.User, retry[^1].Role);
        AssertEx.Contains(retry[^1].Text, "tool call inside your reasoning");
        AssertEx.True(retry.Any(static message => message.Contents.OfType<FunctionResultContent>().Any(static result => result.CallId == "call-1")),
            "the executed call's result is replayed");
        AssertEx.False(retry.Any(static message => message.Contents.OfType<TextReasoningContent>().Any(static reasoning => reasoning.Text.Contains("<tool_call>", StringComparison.Ordinal))),
            "the silent round's reasoning is not replayed");
        AssertEx.Equal("False,False,True,True", string.Join(",", thinkingOffRounds), "thinking goes off at the re-prompt and stays off to the end of the turn");
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EmptyAnswer));
    }

    [Test]
    public async Task RunAsync_WhenTheRepromptAlsoReasonsAToolCall_StopsAfterOneRetryWithTheToolCallNotice()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var calls = 0;
        var factory = CreateMessageCapturingFactory(cancellationToken =>
            {
                _ = Interlocked.Increment(ref calls);
                return ToolRoundThenReasonedToolCall();
            },
            _ => { });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build());

        AssertEx.Equal(2, calls, "bounded to one retry");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EmptyAnswer
            && payload.Message == "The model tried to call a tool inside its reasoning, where the call cannot run, and stopped without an answer."));
    }

    [Test]
    public async Task RunAsync_WhenTheSilentRoundReasonsNoToolCall_DoesNotReprompt()
    {
        var calls = 0;
        var factory = CreateMessageCapturingFactory(cancellationToken =>
            {
                _ = Interlocked.Increment(ref calls);
                return ToolRoundUpdates("ok", finalText: null);
            },
            _ => { });
        var runner = CreateRunner(factory);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build());

        AssertEx.Equal(1, calls);
    }

    /// <summary>
    ///     After the budget closes the reasoning block a 9B can keep deliberating in content and close it
    ///     with a literal <c>&lt;/think&gt;</c>. The persisted answer is what follows the tag; the text before it is reasoning.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenTheAnswerCarriesAStrayCloseTag_ReclassifiesTheTextBeforeItAsReasoning()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: ReasoningThenContent("budget ran out", "Another option: hi.\nI will answer.\n", "</think>", "\n\nHello there."));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationTextReclassifiedAsync(package.InvocationId,
            "Hello there.",
            "\n\nAnother option: hi.\nI will answer.");
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EmptyAnswer));
    }

    /// <summary>Live on LFM2.5 at effort none: the whole reply was "Okay." and a stray close tag. The text stays the answer.</summary>
    [Test]
    public async Task RunAsync_WhenTheOnlyTextPrecedesAStrayCloseTag_KeepsItAsTheAnswerWithoutTheTag()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates("Okay.\n", "</think>"));
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("none").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationTextReclassifiedAsync(package.InvocationId, "Okay.", string.Empty);
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EmptyAnswer));
    }

    /// <summary>The same rule after reasoning, and for a whole block with nothing outside it: the round never loses its only text.</summary>
    [Test]
    [Arguments("still deliberating", "</think>")]
    [Arguments("<think>still deliberating", "</think>")]
    public async Task RunAsync_WhenMovingTheLeakedTextWouldLeaveNoAnswer_KeepsItAsTheAnswer(string first, string second)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ReasoningThenContent("budget ran out", first, second));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationTextReclassifiedAsync(package.InvocationId, "still deliberating", string.Empty);
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EmptyAnswer));
    }

    /// <summary>Model-matrix V2: the 0.8B reopened the think block in content after the budget. The block is reasoning.</summary>
    [Test]
    public async Task RunAsync_WhenAThinkingModelsAnswerCarriesAWholeThinkBlock_ReclassifiesTheBlockAsReasoning()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: ReasoningThenContent("budget ran out", "<think>\n\nThinking Process: greet back.", "\n</think>\n\n", "Hello there."));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationTextReclassifiedAsync(package.InvocationId, "Hello there.", "\n\nThinking Process: greet back.");
    }

    /// <summary>A model at effort none (LFM2.5) can close in-content deliberation with a literal close tag and reason nothing.</summary>
    [Test]
    public async Task RunAsync_WhenAThinkingModelLeaksAStrayCloseTagWithThinkingOff_ReclassifiesTheTextBeforeIt()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates("User asks: 17 + 25. Provide final.\n", "</think>", "\n42"));
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("none").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationTextReclassifiedAsync(package.InvocationId, "42", "User asks: 17 + 25. Provide final.");
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EmptyAnswer));
    }

    /// <summary>A model that cannot think and explains the tags keeps its answer, quoted block or stray close tag alike.</summary>
    [Test]
    [Arguments("<think>a tag</think> is markup")]
    [Arguments("A closing </think> ends the block.")]
    public async Task RunAsync_WhenANonThinkingModelWritesThinkTags_LeavesTheAnswerAlone(string answer)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates(answer));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build() with
        {
            SupportsThinking = false
        });

        await dispatcher.DidNotReceive().ReportInvocationTextReclassifiedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>A thinking model that explains the tags after answer text keeps them: only a block that opens the answer is residue.</summary>
    [Test]
    [Arguments("Use <think>analysis</think> before the answer.")]
    [Arguments("The template:\n```\n<think>analysis</think>\nanswer\n```")]
    public async Task RunAsync_WhenAThinkingModelQuotesAThinkBlockAfterAnswerText_LeavesTheAnswerAlone(string answer)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ReasoningThenContent("thought", answer));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.DidNotReceive().ReportInvocationTextReclassifiedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task RunAsync_WhenTheAnswerHasNoThinkTag_LeavesItAlone()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ReasoningThenContent("thought", "No tag at all."));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.DidNotReceive().ReportInvocationTextReclassifiedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    [Arguments("stop", false)]
    [Arguments("length", false)]
    public async Task RunAsync_WhenTheEmptyTurnWasNotCutOffWhileThinking_KeepsThePlainNotice(string finishReason, bool withReasoning)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ThinkingThenFinish(finishReason, withReasoning));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EmptyAnswer
            && payload.Message == "The model stopped without an answer."
            && payload.Detail == finishReason));
    }

    /// <summary>
    ///     The runner tracks the turn under its assistant message id for "Answer now": while the armed completion is
    ///     reasoning the control reaches llama-server, and once the turn is over the message is no longer addressable.
    /// </summary>
    [Test]
    public async Task RunAsync_WhileAnArmedCompletionReasons_AnswerNowReachesItsServer()
    {
        var native = Substitute.For<ILlamaServerNativeClient>();
        native.EndReasoningAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new LlamaServerReasoningControlResult
              {
                  Success = true
              });
        var reasoningControl = new InvocationReasoningControl(native, NullLogger<InvocationReasoningControl>.Instance);
        var reasoning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(agentUpdates: ArmedReasoningUpdates(reasoning, release), reasoningControl: reasoningControl);
        var messageId = Guid.NewGuid();

        var run = runner.RunAsync(InvocationExecutionContext.CreatePlain(RuntimePackageBuilder.Valid().Build(), messageId));
        await AssertEx.CompletesAsync(reasoning.Task, TimeSpan.FromSeconds(30), "the stream must reach its reasoning phase.");
        await AssertEx.EventuallyAsync(async () => await reasoningControl.EndReasoningAsync(messageId, CancellationToken.None) == ReasoningEndOutcome.Ended,
            TimeSpan.FromSeconds(30),
            "the reasoning update has been folded once the next pull is parked.");
        release.SetResult();
        await AssertEx.CompletesAsync(run, TimeSpan.FromSeconds(30), "the turn must finish once released.");

        await native.Received().EndReasoningAsync(new Uri("http://127.0.0.1:5812/v1"), "chatcmpl-runner", Arg.Any<CancellationToken>());
        AssertEx.Equal(ReasoningEndOutcome.NotFound, await reasoningControl.EndReasoningAsync(messageId, CancellationToken.None));
    }

    [Test]
    public async Task RunAsync_WhenTheLastRoundAnswers_EmitsNoEmptyAnswerNotice()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ToolRoundUpdates("ok", finalText: "done"));

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build());

        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EmptyAnswer));
    }

    [Test]
    public async Task RunAsync_SeedsTheToolResultBudgetFromTheModelWindow()
    {
        int? budgetDuringTheRun = null;
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CapturingBudget(value => budgetDuringTheRun = value));

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        var window = (int)dispatcher.ReceivedCalls()
                                    .Single(call => call.GetMethodInfo().Name == nameof(IWorkerEventDispatcher.ReportTurnContextWindowAsync))
                                    .GetArguments()[1]!;
        AssertEx.Equal(Math.Clamp(window, 8_192, 65_536), budgetDuringTheRun, "Tool results are sized to the window the model was launched with.");
        AssertEx.Null(ToolResultBudgetScope.Current, "The scope ends with the turn.");
    }

    [Test]
    [Arguments(0, 8_192)]
    [Arguments(1_000, 8_192)]
    [Arguments(32_768, 32_768)]
    [Arguments(262_144, 65_536)]
    public async Task ToolResultBudgetFor_FloorsAnUnknownOrTinyWindow_AndCapsALargeOne(int windowTokens, int expected)
    {
        // An unknown window reported as 0 clamped to a one-character budget, so every tool result arrived as a marker.
        AssertEx.Equal(expected, InvocationRunner.ToolResultBudgetFor(windowTokens));
        await Task.CompletedTask;
    }

    // One tool call and its result, then either a final answer or a silent round (null).
    [Test]
    public async Task RunAsync_WhenOneUpdateCarriesNarrationAndAToolCall_ReportsTheNarrationBeforeTheRequestedCall()
    {
        // The requested call's content offset is stamped at report time, so the narration riding the same update must
        // already be streamed, or it lands in the answer after the card.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var order = new List<string>();
        dispatcher.When(static call => call.ReportInvocationThinkingChunkAsync(Arg.Any<Guid>(), Arg.Any<string>()))
                  .Do(call => order.Add("reasoning:" + call.ArgAt<string>(1)));
        dispatcher.When(static call => call.ReportInvocationStreamChunkAsync(Arg.Any<Guid>(), Arg.Any<string>()))
                  .Do(call => order.Add("text:" + call.ArgAt<string>(1)));
        dispatcher.When(static call => call.ReportToolCallLifecycleAsync(Arg.Any<ToolCallLifecyclePayload>()))
                  .Do(call => order.Add("tool:" + call.ArgAt<ToolCallLifecyclePayload>(0).Phase));
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: NarratedToolCallUpdates());

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build());

        AssertEx.Equal("reasoning:plan|text:Let me search. |tool:Requested|tool:Completed|text:done",
            string.Join('|', order));
    }
}
