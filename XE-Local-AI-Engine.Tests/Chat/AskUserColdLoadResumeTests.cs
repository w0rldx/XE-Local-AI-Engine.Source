namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Contracts.Events;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The COLD-LOAD re-attach chain for a turn parked on an <c>ask_user</c> question, exercised across the real
///     <see cref="WorkerEventDispatcher" /> and the real <see cref="InvocationResumeRegistry" /> rather than either in
///     isolation.
///     <para>
///         This closes a genuine coverage seam. <c>InvocationRunnerTests</c> proves the runner parks and resumes, but
///         against a SUBSTITUTED dispatcher, so nothing there ever records a pending question on an
///         <see cref="InvocationState" />. <c>InvocationResumeRegistryTests</c> proves the replay, but from a
///         HAND-BUILT state, so nothing there proves the dispatcher would ever produce such a state. The defect this
///         guards against lives precisely in the join: a reloaded browser holds no invocation id, so it must find the
///         live run by CONVERSATION id, and the prompt it needs is transient live state that is deliberately never
///         written into the conversation's persisted parts. Break any link and a reload silently strands the turn until
///         it times out — which is exactly what shipped before D6, and what a live reload reproduced.
///     </para>
/// </summary>
public sealed class AskUserColdLoadResumeTests
{
    [Test]
    public async Task AfterAReload_TheQuestionIsFoundByConversation_Replayed_AndAnsweringReleasesTheParkedRun()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var registry = CreateRegistry(dispatcher);

        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var package = RuntimePackageBuilder.Valid()
                                           .WithInvocationId(invocationId)
                                           .WithConversationId(conversationId)
                                           .WithAllowedTool(AskUserTool.ToolName)
                                           .Build();

        // The turn starts and then parks on the operator, exactly as RequestUserAnswerAsync drives it.
        await using var lease = await dispatcher.ReportInvocationAssignedAsync(package);
        await dispatcher.ReportUserQuestionAsync(new UserQuestionLifecyclePayload
        {
            InvocationId = invocationId,
            RequestId = "question-1",
            CallId = "call-1",
            ToolName = AskUserTool.ToolName,
            Questions =
            [
                new UserQuestionSpec("Auth method",
                    "Which auth method?",
                    MultiSelect: false,
                    [
                        new UserQuestionOption("OAuth device flow", "No password to store.", Recommended: true),
                        new UserQuestionOption("API key", Description: null, Recommended: false)
                    ])
            ]
        });

        // THE COLD-LOAD ENTRY POINT: a reloaded page has no invocation id, only the conversation it reopened.
        var resolved = registry.TryGetLiveInvocationIdForConversation(conversationId);
        AssertEx.True(resolved.HasValue, "a reloaded client must be able to find the live run from the conversation alone");
        AssertEx.Equal(invocationId, resolved!.Value);

        var resumeId = resolved.Value;
        var events = new List<ChatStreamEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(resumeId, cancellation.Token))
            {
                events.Add(streamEvent);
            }
        }, cancellation.Token);

        await AssertEx.EventuallyAsync(() => events.Any(evt => evt.Type == ChatStreamEventTypes.QuestionRequested), TimeSpan.FromSeconds(10));

        var replayed = events.Single(evt => evt.Type == ChatStreamEventTypes.QuestionRequested);
        AssertEx.Equal("question-1", replayed.QuestionRequestId);
        AssertEx.Equal("call-1", replayed.ToolCallId);
        AssertEx.Equal(AskUserTool.ToolName, replayed.ToolName);
        // The questions must ride the replay: an id alone cannot be rendered into an answerable prompt, which is the
        // whole reason the pending slot carries them rather than just a correlation key.
        AssertEx.Contains(replayed.Questions, "Which auth method?");
        AssertEx.Contains(replayed.Questions, "OAuth device flow");

        // Answering from the re-attached client must release the run that is still parked server-side.
        var answers = new[] { new UserQuestionAnswer("Which auth method?", ["OAuth device flow"], Other: null) };
        await dispatcher.DispatchUserQuestionAnsweredAsync(new UserQuestionAnsweredEvent("question-1", answers));

        runner.Received(1).ResolveUserQuestionResult(Arg.Is<UserQuestionAnsweredEvent>(evt =>
            evt.RequestId == "question-1" && evt.Answers.Count == 1 && evt.Answers[0].Selected[0] == "OAuth device flow"));

        // The slot clears, so a client attaching after the answer is not handed a prompt that has already been resolved.
        AssertEx.Null(registry.TryGetLiveInvocation(invocationId)?.PendingQuestion);

        await dispatcher.ReportInvocationCompletedAsync(invocationId);
        await consumer;
    }

    [Test]
    public async Task WhenNothingIsLiveForTheConversation_TheColdLoadLookupIsANoOp()
    {
        // The hub calls this on EVERY conversation open, so an idle conversation must resolve to nothing rather than
        // throwing — otherwise simply opening a finished chat would surface an error.
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var registry = CreateRegistry(dispatcher);

        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithConversationId(conversationId).Build();

        AssertEx.Null(registry.TryGetLiveInvocationIdForConversation(conversationId));

        await using var lease = await dispatcher.ReportInvocationAssignedAsync(package);
        AssertEx.Equal(invocationId, registry.TryGetLiveInvocationIdForConversation(conversationId)!.Value);

        // A DIFFERENT conversation never resolves to this run, or a reload of chat B would hijack chat A's turn.
        AssertEx.Null(registry.TryGetLiveInvocationIdForConversation(Guid.NewGuid()));

        await dispatcher.ReportInvocationCompletedAsync(invocationId);
        AssertEx.Null(registry.TryGetLiveInvocationIdForConversation(conversationId));
    }

    private static WorkerEventDispatcher CreateDispatcher(IInvocationRunner runner)
    {
        return new WorkerEventDispatcher(runner,
            Substitute.For<IRuntimePackageEnvelopeAssembler>(),
            new Lazy<IHubMessageSender>(static () => Substitute.For<IHubMessageSender>()),
            Substitute.For<INodeKeyRegistry>(),
            Substitute.For<IInvocationHistory>(),
            Substitute.For<INodeChatRemotePersistenceCoordinator>(),
            NullLogger<WorkerEventDispatcher>.Instance);
    }

    private static InvocationResumeRegistry CreateRegistry(IWorkerEventDispatcher dispatcher)
    {
        return new InvocationResumeRegistry(dispatcher, TimeProvider.System, NullLogger<InvocationResumeRegistry>.Instance);
    }
}
