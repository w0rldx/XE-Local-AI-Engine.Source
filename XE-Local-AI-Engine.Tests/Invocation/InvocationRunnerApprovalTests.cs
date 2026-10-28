namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Invocation.InvocationRunnerTestSupport;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

[Category(TestCategories.Unit)]
public sealed class InvocationRunnerApprovalTests
{
    [Test]
    public async Task RunAsync_WhenToolApprovalRequested_SendsApprovalThenResumesAfterDecision()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        // The package must offer a tool: an approval request can only surface for a tool-bearing turn, and the runner
        // only retains the segment updates it folds on resume when the offer list is non-empty (see approvalPossible).
        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());

        // The approval card's opaque request id is published only on the dispatcher now that no hub send carries it.
        string? requestId = null;
        await AssertEx.EventuallyAsync(() =>
        {
            var reported = dispatcher.ReceivedCalls()
                                     .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(IWorkerEventDispatcher.ReportApprovalRequestedAsync), StringComparison.Ordinal))
                                     .Select(call => (ApprovalRequestPayload)call.GetArguments()[0]!)
                                     .FirstOrDefault();
            requestId = reported?.RequestId;
            return requestId is not null;
        }, TimeSpan.FromSeconds(5));

        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = requestId!,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the runner must re-invoke the agent threadlessly after the approval decision");
        await dispatcher.Received(1).ReportApprovalRequestedAsync(Arg.Is<ApprovalRequestPayload>(payload => payload.InvocationId == invocationId));
    }

    [Test]
    public async Task RunAsync_WhenAnApprovalIsDenied_GivesTheSlotBackWhileParked_AndRetakesItBeforeTheModelRunsAgain()
    {
        // A deny still calls the model (it is told the call was refused), so the turn must hold the slot again first.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReleaseSlotForPark(Arg.Any<Guid>()).Returns(true);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());
        var requestId = await ReportedApprovalRequestIdAsync(dispatcher);

        dispatcher.Received(1).ReleaseSlotForPark(invocationId);
        await dispatcher.DidNotReceive().ReacquireSlotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = requestId,
            Approved = false
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the denied call resumes the turn");
        Received.InOrder(() =>
        {
            dispatcher.ReleaseSlotForPark(invocationId);
            dispatcher.ReacquireSlotAsync(invocationId, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RunAsync_WhenAToolCallIsDenied_TheModelReadsWhyAndTheToolIsWithheldForTheRestOfTheTurnOnly()
    {
        // After a denial a model can ask for the same call again and finally invent its result. It must read that nothing ran,
        // must not be offered the tool again in this turn, and must be offered it again in the next turn.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        using var model = new DenialScriptedChatClient();
        var runner = CreateRunner(DenialPipelineFactory(model), eventDispatcher: dispatcher);
        var conversationId = Guid.NewGuid();

        var firstTurn = RunAsync(runner, DenialPackage(conversationId));
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(10));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[0].RequestId,
            Approved = false
        });
        await firstTurn.WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
        AssertEx.Equal(expected: 2, model.OfferedTools.Count, "the denied call resumes the turn once");
        AssertEx.Contains(model.OfferedTools[0], DenialScriptedChatClient.GuardedToolName);
        AssertEx.False(model.OfferedTools[1].Contains(DenialScriptedChatClient.GuardedToolName, StringComparer.Ordinal),
            "the round after the denial must not offer the denied tool");
        AssertEx.Contains(model.OfferedTools[1], DenialScriptedChatClient.OtherToolName);
        var result = AssertEx.NotNull(model.ToolResult);
        AssertEx.True(result.StartsWith(ToolInvocationObservabilityChatClient.RejectedApprovalResultPrefix, StringComparison.Ordinal),
            $"the framework prefix the observability client keys Outcome=denied on must lead the result: {result}");
        AssertEx.Contains(result, InvocationRunner.DeniedToolCallReason);
        AssertEx.Contains(result, ToolFailureText.NotRunNotice);
        // The chat card reads Denied because the completed phase says so, not because the client matches the text.
        await dispatcher.Received().ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(static payload =>
            payload.Phase == ToolCallLifecyclePhase.Completed && payload.IsDenied && !payload.IsError));

        var secondTurn = RunAsync(runner, DenialPackage(conversationId));
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(10));
        AssertEx.Contains(model.OfferedTools[2], DenialScriptedChatClient.GuardedToolName);
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = false
        });
        await secondTurn.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task RunAsync_WhenOneCallOfAToolIsApprovedAndAnotherDeniedInOneSegment_TheApprovedCallRuns()
    {
        // Withholding the denied tool before the replay round would hide it from the function invoker running the approved call.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        using var model = new DenialScriptedChatClient(callsPerRound: 2);
        var runner = CreateRunner(DenialPipelineFactory(model), eventDispatcher: dispatcher);

        var turn = RunAsync(runner, DenialPackage(Guid.NewGuid()));
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(10));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[0].RequestId,
            Approved = true
        });
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(10));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = false
        });
        await turn.WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
        AssertEx.Equal(expected: 2, model.OfferedTools.Count);
        AssertEx.Contains(model.OfferedTools[1], DenialScriptedChatClient.GuardedToolName);
        AssertEx.Equal(expected: 2, model.ToolResults.Count, string.Join(" | ", model.ToolResults));
        AssertEx.Contains(model.ToolResults, "ran");
        AssertEx.True(model.ToolResults.Exists(static result => result.StartsWith(ToolInvocationObservabilityChatClient.RejectedApprovalResultPrefix, StringComparison.Ordinal)
                                                                && result.Contains(InvocationRunner.DeniedToolCallReason, StringComparison.Ordinal)),
            $"the denied call still reads the reason: {string.Join(" | ", model.ToolResults)}");
    }

    [Test]
    public async Task RunAsync_WhenAToolCallIsApproved_TheToolStaysOfferedForTheRestOfTheTurn()
    {
        // The control for the withhold: an approved call changes nothing about the offer.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        using var model = new DenialScriptedChatClient();
        var runner = CreateRunner(DenialPipelineFactory(model), eventDispatcher: dispatcher);

        var turn = RunAsync(runner, DenialPackage(Guid.NewGuid()));
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(10));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[0].RequestId,
            Approved = true
        });
        await turn.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, model.OfferedTools.Count);
        AssertEx.Contains(model.OfferedTools[1], DenialScriptedChatClient.GuardedToolName);
        AssertEx.Equal("ran", model.ToolResult);
    }

    [Test]
    public async Task RunAsync_WhenAnApprovalExpires_FailsTheTurnWithoutRetakingTheSlot()
    {
        var time = new ManualTimeProvider();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReleaseSlotForPark(Arg.Any<Guid>()).Returns(true);
        var runner = CreateRunner(CreateFactory(_ => ApprovalRequestUpdates()), eventDispatcher: dispatcher, coordinatorTimeProvider: time);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());
        _ = await ReportedApprovalRequestIdAsync(dispatcher);
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount > 0, TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await runTask;

        await dispatcher.Received(1).ReportInvocationFailedAsync(invocationId, Arg.Any<string>(), FailureCategory.Timeout);
        await dispatcher.DidNotReceive().ReacquireSlotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenAnAskUserQuestionTimesOut_RetakesTheSlotAndContinuesUnanswered()
    {
        var time = new ManualTimeProvider();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReleaseSlotForPark(Arg.Any<Guid>()).Returns(true);
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, coordinatorTimeProvider: time);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool(AskUserTool.ToolName).Build());
        await AssertEx.EventuallyAsync(() => question is not null && time.ArmedTimerCount > 0, TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await runTask;

        AssertEx.Equal(expected: 2, segment, "an unanswered question still resumes the turn");
        Received.InOrder(() =>
        {
            dispatcher.ReleaseSlotForPark(invocationId);
            dispatcher.ReacquireSlotAsync(invocationId, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RunAsync_WhenAnAnsweredTurnWaitsForTheSlotPastItsParkedDeadline_FailsAsATimeout()
    {
        // Pinned behavior: the resume wait is bounded by the parked deadline, which stays
        // armed until the turn holds the slot again, so a late answer behind a long holder ends the reply as a timeout.
        var time = new ManualTimeProvider();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReleaseSlotForPark(Arg.Any<Guid>()).Returns(true);
        dispatcher.ReacquireSlotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                  .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()));
        var runner = CreateRunner(CreateFactory(_ => ApprovalRequestUpdates()), eventDispatcher: dispatcher, timeProvider: time);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithTimeout(invocationSeconds: 300)
                                                            .WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = await ReportedApprovalRequestIdAsync(dispatcher),
            Approved = true
        });
        await AssertEx.EventuallyAsync(() => dispatcher.ReceivedCalls().Any(call => string.Equals(call.GetMethodInfo().Name,
                nameof(IWorkerEventDispatcher.ReacquireSlotAsync),
                StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        // The parked backstop: the five-minute pending age plus the 300 s turn budget.
        time.Advance(TimeSpan.FromSeconds(601));
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.Received(1).ReportInvocationFailedAsync(invocationId,
            Arg.Is<string>(message => message.StartsWith("Timed out", StringComparison.Ordinal)),
            FailureCategory.Timeout);
    }

    [Test]
    public async Task RunAsync_WhenAClientLocalToolRequiresApproval_StillFoldsTheSegmentAndResumes()
    {
        // Companion to the test above (whose offer is ApiSide, i.e. carries no approval metadata to the tool resolver
        // and so counts as approval-possible by fail-closed). This is the OTHER branch of the retention predicate: a
        // ClientLocal offer is judged on its own RequiresApproval flag, so a true one must still retain and fold the
        // segment. A predicate that narrowed to "any tool offered" vs "any tool that can be approval-wrapped" must not
        // lose this case — dropping the retention here would replay an approval resume without its assistant tool-call.
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        var package = RuntimePackageBuilder.Valid().Build();
        package.AllowedTools.Add(new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = "run_in_agent_home",
            Location = ToolLocation.ClientLocal,
            RequiresApproval = true
        });

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));

        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "an approval-required ClientLocal tool must still drive the fold-and-resume segment");
    }

    [Test]
    public async Task ResolveApprovalResult_WhenRequestIdIsUnmatched_DoesNotResumeThePendingApproval()
    {
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner,
            RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));

        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = $"unmatched-{Guid.NewGuid():N}",
            Approved = true
        });

        AssertEx.False(runTask.IsCompleted, "an unmatched approval response must not resume the held invocation");
        AssertEx.Equal(expected: 1, segment, "an unmatched approval response must not start the resume segment");

        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the matching approval response must resume the invocation");
    }

    [Test]
    public async Task ResolveApprovalResult_WhenDecisionIsDuplicated_FirstDecisionWins()
    {
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ =>
            {
                segment++;
                return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
            },
            messages => resumeMessages = messages);
        // The approval card's opaque request id is published only on the dispatcher now that no hub send carries it.
        string? requestId = null;
        var approvalDispatcher = Substitute.For<IWorkerEventDispatcher>();
        approvalDispatcher.ReportApprovalRequestedAsync(Arg.Do<ApprovalRequestPayload>(payload => requestId = payload.RequestId))
                          .Returns(Task.CompletedTask);
        var runner = CreateRunner(factory, eventDispatcher: approvalDispatcher);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner,
            RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());

        await AssertEx.EventuallyAsync(() => requestId is not null, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = requestId!,
            Approved = false
        });
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = requestId!,
            Approved = true
        });
        await runTask;

        var response = AssertEx.NotNull(resumeMessages)
                               .SelectMany(static message => message.Contents)
                               .OfType<ToolApprovalResponseContent>()
                               .Single();
        AssertEx.False(response.Approved, "a duplicate response must not overwrite the first approval decision");
        AssertEx.Equal(expected: 2, segment, "the duplicate response must not trigger a second resume segment");
    }

    [Test]
    public async Task RunAsync_WhenSkillApprovalIsGrantedForTheSession_SuppressesTheNextPromptAndStillAudits()
    {
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(SkillApprovalFactory(LoadSkillToolName, SkillName), approvalAuditRecorder: auditRecorder, eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        // A SECOND turn in the SAME conversation, on the same skill at the same version: the memo answers it.
        await RunAsync(runner, SkillPackage(conversationId).Build());

        AssertEx.Equal(expected: 1, approvals.Count, "a session-scoped approval must not prompt again for the same skill in the same conversation");
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               LoadSkillToolName,
                               ToolCategory.ReadLocal,
                               "session-scope auto-approve",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenRunSkillScriptIsApprovedForTheSession_PromptsAgain()
    {
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(SkillApprovalFactory(RunSkillScriptToolName, SkillName), approvalAuditRecorder: auditRecorder, eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "script execution is outside the memo allow-list and must be approved every single time");

        // The other half of the audit fix: a provider-injected tool that is not in the package offer must still audit
        // under its real risk category rather than the fail-closed Unknown.
        await auditRecorder.Received(2)
                           .RecordAsync(Arg.Any<Guid?>(),
                               RunSkillScriptToolName,
                               ToolCategory.WriteExecute,
                               "approve",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RunAsync_SkillOnlyAgent_LoadSkillDecision_ResumesThroughTheRealApprovalValidator(bool approved)
    {
        // A skill-only agent offers load_skill outside AllowedTools; a resume that replays the response without the
        // request makes ApprovalResponseValidatingAgent fail the turn whichever way the card was answered.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        using var model = new LoadSkillScriptedChatClient();
        var runner = CreateRunner(RealSkillPipelineFactory(model), eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid()
                                           .WithConversationId(Guid.NewGuid())
                                           .WithSkills(new ResolvedSkill(SkillId, SkillName, "A skill.", LoadSkillScriptedChatClient.SkillBody, 1, false))
                                           .Build();
        AssertEx.Empty(package.AllowedTools, "the scenario is a skill-only agent; an offered tool would mask the defect");

        var run = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(10));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = approved
        });
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
        AssertEx.Equal("all done", model.FinalText);
        AssertEx.Equal(approved, AssertEx.NotNull(model.LoadSkillResult).Contains(LoadSkillScriptedChatClient.SkillBody, StringComparison.Ordinal),
            "an approved load_skill returns the skill body to the model, a denied one does not");
    }

    [Test]
    public async Task RunAsync_WhenApprovalIsRequested_TheEventSaysWhetherSessionScopeCanBeRemembered()
    {
        // The node TOOL CATALOG cannot decide the card's "Approve for this session" button: it does not carry the MAF
        // skill tools, so run_skill_script and imported skills would offer a session scope the runner never memoizes,
        // and the click would silently degrade to a plain "Once". The runner already resolves the memo key before
        // broadcasting, so it publishes that answer on the approval event itself.
        AssertEx.Equal(expected: false, await CaptureSessionScopeEligibleAsync(SkillApprovalFactory(RunSkillScriptToolName, SkillName), SkillPackage(Guid.NewGuid())),
            "script execution is outside the memo allow-list, so the card must not offer a session scope.");
        AssertEx.Equal(expected: false,
            await CaptureSessionScopeEligibleAsync(SkillApprovalFactory(LoadSkillToolName, SkillName), SkillPackage(Guid.NewGuid(), imported: true)),
            "an imported skill is never session-approvable — a per-CALL narrowing the tool catalog cannot see.");
        AssertEx.Equal(expected: true, await CaptureSessionScopeEligibleAsync(CustomToolApprovalFactory(CustomToolName), CustomToolPackage(Guid.NewGuid(), isFixed: true)),
            "a Fixed custom tool IS memoized, so the button must still be offered.");
    }

    [Test]
    public async Task RunAsync_WhenAnUnattendedRunNeedsApproval_FailsImmediatelyWithTheReason()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        string? failureMessage = null;
        FailureCategory? failureCategory = null;
        dispatcher.ReportInvocationFailedAsync(Arg.Any<Guid>(),
                      Arg.Do<string>(message => failureMessage = message),
                      Arg.Do<FailureCategory>(category => failureCategory = category))
                  .Returns(Task.CompletedTask);
        var runner = CreateRunner(SkillApprovalFactory(LoadSkillToolName, SkillName), eventDispatcher: dispatcher);

        // real-timer: the pending-approval window is FIVE MINUTES in this fixture (see CreateRunner); the elapsed
        // ceiling is the fail-fast behavior under test, far below that window and far above any scheduling delay.
        var elapsed = Stopwatch.StartNew();
        await RunAsync(runner, SkillPackage(Guid.NewGuid()).AsUnattended().Build());
        elapsed.Stop();

        AssertEx.Equal(FailureCategory.AgentRuntime, failureCategory);
        AssertEx.True(AssertEx.NotNull(failureMessage).Contains($"approval required in an unattended run: {LoadSkillToolName}", StringComparison.Ordinal),
            $"the failure must name the cause, not read as a generic timeout; got '{failureMessage}'");
        AssertEx.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"the unattended guard must fail fast, not wait out the approval window; took {elapsed.Elapsed}");
    }

    [Test]
    public async Task RunAsync_WhenUnattendedAskUserQuestionSurfaces_ContinuesImmediatelyWithoutAnAnswer()
    {
        // The asymmetry with the approval path, asserted: an unattended APPROVAL fails the turn fast, an unattended
        // QUESTION continues with "not answered" — and neither one waits out the pending-approval window.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, userQuestionAnswerStash: stash);

        // real-timer: skipping the five-minute question park is the behavior; the 30 s ceiling cannot be met by waiting.
        var elapsed = Stopwatch.StartNew();
        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).AsUnattended().Build());
        elapsed.Stop();

        AssertEx.Equal(expected: 2, segment, "the turn must continue threadlessly rather than fail for an unattended run");
        await dispatcher.DidNotReceive().ReportUserQuestionAsync(Arg.Any<UserQuestionLifecyclePayload>());
        AssertEx.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
            $"the unattended run must skip the park, not wait out the 5-minute question cap; took {elapsed.Elapsed}");

        AssertEx.True(stash.TryPop("call-ask-user", out var stashed), "the model still needs a branchable result under the tool call's CallId");
        AssertEx.Contains(stashed, "\"answered\":false");
        AssertEx.Contains(stashed, "\"reason\":\"unattended\"");
    }

    [Test]
    public async Task RunAsync_WhenAskUserQuestionSurfaces_PromptsTheOperatorAndResumesWithTheAnswer()
    {
        // ask_user rides the approval seam for its BLOCKING behavior, not for a risk verdict: the runner must present
        // the QUESTIONS (not an approve/deny card), park, then always approve so the framework executes the tool and the
        // handler returns the stashed answer as the tool result.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ =>
            {
                segment++;
                return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
            },
            messages => resumeMessages = messages);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, userQuestionAnswerStash: stash);

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build());
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));

        var surfaced = AssertEx.NotNull(question);
        AssertEx.Equal(AskUserTool.ToolName, surfaced.ToolName);
        AssertEx.Equal("call-ask-user", surfaced.CallId, "the question card must attach to the tool-call card the model is waiting on");
        AssertEx.Equal("Which auth method?", surfaced.Questions.Single().Question);
        AssertEx.True(surfaced.Questions.Single().Options[0].Recommended);
        AssertEx.False(runTask.IsCompleted, "the turn must hold until the operator answers");

        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = surfaced.RequestId,
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Which auth method?",
                    Selected = ["OAuth device flow"],
                    Other = null
                }
            ]
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the answered question must resume the turn threadlessly");
        var response = AssertEx.NotNull(resumeMessages).SelectMany(static message => message.Contents).OfType<ToolApprovalResponseContent>().Single();
        AssertEx.True(response.Approved, "a question round-trip always approves — the answer travels as the TOOL RESULT, not as an approval verdict");

        AssertEx.True(stash.TryPop("call-ask-user", out var stashed), "the answer must be stashed under the tool call's CallId for the handler to pop");
        AssertEx.Contains(stashed, "\"answered\":true");
        AssertEx.Contains(stashed, "OAuth device flow");
    }

    [Test]
    public async Task RunAsync_WhenAWebFetchSurfaces_RetrievesItParksOnAReviewCardAndTheResumedHandlerReturnsTheAcceptedResult()
    {
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        var handlerResults = new ConcurrentQueue<string>();
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ => ++segment == 1 ? WebFetchRequestUpdates() : ExecuteWebFetchHandlerUpdates(handlerResults),
            messages => resumeMessages = messages);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true).Build());
        var consent = await ResolveWebCardAsync(runner, cards, expectedCount: 1, approved: true);
        AssertEx.Equal("call-web-fetch", consent.CallId);
        var consentPreview = AssertEx.NotNull(consent.WebReview);
        AssertEx.Equal(WebReviewPreview.RequestStage, consentPreview.Stage);
        AssertEx.Equal(WebReviewTestServer.PageUrl, consentPreview.Url);
        await AssertEx.EventuallyAsync(() => cards.Count == 2, TimeSpan.FromSeconds(5));

        var card = cards.Last();
        AssertEx.Equal("call-web-fetch", card.CallId, "the review attaches to the tool-call card the model is waiting on");
        var reviewPreview = AssertEx.NotNull(card.WebReview);
        AssertEx.Equal(WebReviewPreview.ResultStage, reviewPreview.Stage);
        AssertEx.Equal(WebReviewTestServer.PageText, reviewPreview.Text);
        AssertEx.Equal(WebReviewTestServer.PageUrl, server.Requests.Single().AbsoluteUri, "fetched once, after consent and before the review, with no allow-list");
        AssertEx.False(runTask.IsCompleted, "the turn holds until the user reviews the content");

        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = card.RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment);
        AssertEx.True(AssertEx.NotNull(resumeMessages).SelectMany(static message => message.Contents).OfType<ToolApprovalResponseContent>().Single().Approved);
        var result = handlerResults.Single();
        AssertEx.Contains(result, WebReviewTestServer.PageText);
        AssertEx.True(result.Contains(UntrustedContentFraming.UntrustedTrustLabel, StringComparison.Ordinal), "accepted content still reaches the model fenced");
    }

    [Test]
    public async Task RunAsync_WhenAWebReviewIsRejected_TheCallIsStillApprovedAndTheHandlerReturnsTheDeclineText()
    {
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var handlerResults = new ConcurrentQueue<string>();
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ => ++segment == 1 ? WebFetchRequestUpdates() : ExecuteWebFetchHandlerUpdates(handlerResults),
            messages => resumeMessages = messages);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, approvalAuditRecorder: auditRecorder, webReviewRetriever: server.CreateRetriever());

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true).Build());
        await ResolveWebCardAsync(runner, cards, expectedCount: 1, approved: true);
        await ResolveWebCardAsync(runner, cards, expectedCount: 2, approved: false);
        await runTask;

        AssertEx.True(AssertEx.NotNull(resumeMessages).SelectMany(static message => message.Contents).OfType<ToolApprovalResponseContent>().Single().Approved,
            "a rejected review still approves the call, so the model gets our decline text rather than a framework rejection");
        var result = handlerResults.Single();
        AssertEx.Contains(result, ToolApprovalCoordinator.WebContentDeclinedMessage);
        AssertEx.False(result.Contains(WebReviewTestServer.PageText, StringComparison.Ordinal));
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               WebFetchToolDefinition.ToolName,
                               Arg.Any<ToolCategory>(),
                               ApprovalDecisions.Deny,
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenAutoAcceptIsOn_NeverSurfacesAReviewCard()
    {
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        var handlerResults = new ConcurrentQueue<string>();
        var segment = 0;
        var factory = CreateFactory(_ => ++segment == 1 ? WebFetchRequestUpdates() : ExecuteWebFetchHandlerUpdates(handlerResults));
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true).AutoAcceptingWebContent().Build())
            .WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Empty(cards);
        await dispatcher.DidNotReceive().ReportApprovalRequestedAsync(Arg.Any<ApprovalRequestPayload>());
        AssertEx.Contains(handlerResults.Single(), WebReviewTestServer.PageText);
    }

    [Test]
    public async Task RunAsync_WhenUnattended_FetchesNothingAndTheTurnContinuesWithTheUnavailableText()
    {
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        var handlerResults = new ConcurrentQueue<string>();
        var segment = 0;
        var factory = CreateFactory(_ => ++segment == 1 ? WebFetchRequestUpdates() : ExecuteWebFetchHandlerUpdates(handlerResults));
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true).AsUnattended().Build())
            .WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment, "an unattended web call continues the turn instead of failing it");
        AssertEx.Empty(server.Requests, "nobody can review it, so nothing is fetched");
        AssertEx.Empty(cards);
        AssertEx.Contains(handlerResults.Single(), ToolApprovalCoordinator.WebContentUnattendedMessage);
    }

    [Test]
    public async Task RunAsync_WhenAWebRequestIsDenied_NothingIsSentAndTheHandlerReturnsTheDeclineText()
    {
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var handlerResults = new ConcurrentQueue<string>();
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ => ++segment == 1 ? WebFetchRequestUpdates() : ExecuteWebFetchHandlerUpdates(handlerResults),
            messages => resumeMessages = messages);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, approvalAuditRecorder: auditRecorder, webReviewRetriever: server.CreateRetriever());

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true).Build());
        await ResolveWebCardAsync(runner, cards, expectedCount: 1, approved: false);
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment, "a declined request continues the turn");
        AssertEx.Empty(server.Requests, "a declined request never leaves the node");
        AssertEx.Equal(expected: 1, cards.Count, "nothing was retrieved, so there is no result to review");
        AssertEx.True(AssertEx.NotNull(resumeMessages).SelectMany(static message => message.Contents).OfType<ToolApprovalResponseContent>().Single().Approved,
            "the call is still approved, so the model reads our decline text");
        AssertEx.Contains(handlerResults.Single(), ToolApprovalCoordinator.WebRequestDeclinedMessage);
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               WebFetchToolDefinition.ToolName,
                               Arg.Any<ToolCategory>(),
                               "web-request deny",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenAWebConsentTimesOut_NothingIsSentAndTheTurnContinues()
    {
        // A zero pending age expires the consent the moment it parks: a timeout declines like ask_user, it never fails the turn.
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        var handlerResults = new ConcurrentQueue<string>();
        var segment = 0;
        var factory = CreateFactory(_ => ++segment == 1 ? WebFetchRequestUpdates() : ExecuteWebFetchHandlerUpdates(handlerResults));
        var runner = CreateRunner(factory,
            eventDispatcher: dispatcher,
            webReviewRetriever: server.CreateRetriever(),
            workerOptions: new WorkerNodeOptions
            {
                NodeName = "worker",
                MaxResponseSizeMb = 10,
                MaxPendingToolCallAgeMinutes = 0
            });

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true).Build())
            .WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        AssertEx.Empty(server.Requests);
        AssertEx.Equal(WebReviewPreview.RequestStage, AssertEx.NotNull(cards.Single().WebReview).Stage);
        AssertEx.Contains(handlerResults.Single(), ToolApprovalCoordinator.WebRequestTimeoutMessage);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_FetchDeniedAndSearchAllowedInOneSegment_OnlyTheSearchIsSentAndReviewed()
    {
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        using var model = new WebReviewScriptedChatClient();
        var runner = CreateRunner(RealPipelineFactory(model, stash),
            eventDispatcher: dispatcher,
            userQuestionAnswerStash: stash,
            webReviewRetriever: server.CreateRetriever());
        var package = RuntimePackageBuilder.Valid()
                                           .WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true)
                                           .WithAllowedTool(WebSearchToolDefinition.ToolName, requiresApproval: true)
                                           .WithAllowedTool(AskUserTool.ToolName, requiresApproval: true)
                                           .Build();

        var runTask = RunAsync(runner, package);
        AssertEx.Equal(WebFetchToolDefinition.ToolName, (await ResolveWebCardAsync(runner, cards, expectedCount: 1, approved: false)).ToolName);
        var searchConsent = await ResolveWebCardAsync(runner, cards, expectedCount: 2, approved: true);
        AssertEx.Equal("tidal power", AssertEx.NotNull(searchConsent.WebReview).Query);
        var review = await ResolveWebCardAsync(runner, cards, expectedCount: 3, approved: true);
        AssertEx.Equal(WebSearchToolDefinition.ToolName, review.ToolName);
        AssertEx.Equal(WebReviewPreview.ResultStage, AssertEx.NotNull(review.WebReview).Stage);
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(10));
        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = AssertEx.NotNull(question).RequestId,
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Which auth method?",
                    Selected = ["API key"],
                    Other = null
                }
            ]
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal("all done", model.FinalText);
        AssertEx.False(server.Requests.Any(static uri => uri.AbsoluteUri == WebReviewTestServer.PageUrl), "the denied fetch was never sent");
        AssertEx.Equal(expected: 1, server.Requests.Count, "only the allowed search went out");
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.FetchCallId], ToolApprovalCoordinator.WebRequestDeclinedMessage);
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.SearchCallId], WebReviewTestServer.SearchHitUrl);
        AssertEx.Equal(expected: 3, cards.Count);
    }

    [Test]
    public async Task RunAsync_TwoWebCallsAndAskUserInOneSegment_ThroughTheRealFunctionPipeline_EachReachesTheModelAsReviewed()
    {
        // The real ChatClientAgent + FunctionInvokingChatClient + production tool wrapping: the only proof that the handlers
        // read the runner's AsyncLocal scope and the framework's CallId when they actually execute.
        using var server = new WebReviewTestServer();
        var (dispatcher, cards) = WebReviewDispatcher();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        using var model = new WebReviewScriptedChatClient();
        var runner = CreateRunner(RealPipelineFactory(model, stash),
            eventDispatcher: dispatcher,
            userQuestionAnswerStash: stash,
            webReviewRetriever: server.CreateRetriever());
        var package = RuntimePackageBuilder.Valid()
                                           .WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true)
                                           .WithAllowedTool(WebSearchToolDefinition.ToolName, requiresApproval: true)
                                           .WithAllowedTool(AskUserTool.ToolName, requiresApproval: true)
                                           .Build();

        var runTask = RunAsync(runner, package);
        AssertEx.Equal(WebFetchToolDefinition.ToolName, (await ResolveWebCardAsync(runner, cards, expectedCount: 1, approved: true)).ToolName);
        AssertEx.Empty(server.Requests, "no request is sent until every consent of the segment is answered");
        AssertEx.Equal(WebSearchToolDefinition.ToolName, (await ResolveWebCardAsync(runner, cards, expectedCount: 2, approved: true)).ToolName);
        await AssertEx.EventuallyAsync(() => cards.Count == 3, TimeSpan.FromSeconds(10));
        AssertEx.Equal(expected: 2, server.Requests.Count, "both web calls are retrieved before the first review card");
        AssertEx.Equal(WebReviewPreview.ResultStage, AssertEx.NotNull(cards.Last().WebReview).Stage);
        await ResolveWebCardAsync(runner, cards, expectedCount: 3, approved: true);
        AssertEx.Equal(WebSearchToolDefinition.ToolName, (await ResolveWebCardAsync(runner, cards, expectedCount: 4, approved: false)).ToolName);
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(10));
        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = AssertEx.NotNull(question).RequestId,
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Which auth method?",
                    Selected = ["API key"],
                    Other = null
                }
            ]
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal("all done", model.FinalText);
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.FetchCallId], WebReviewTestServer.PageText);
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.SearchCallId], ToolApprovalCoordinator.WebContentDeclinedMessage);
        AssertEx.False(model.Results[WebReviewScriptedChatClient.SearchCallId].Contains("Ignore previous instructions", StringComparison.Ordinal));
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.AskCallId], "API key");
        AssertEx.Equal(expected: 2, server.Requests.Count, "the resumed segment executes the handlers, which never fetch again");
    }

    [Test]
    public async Task RunAsync_WhenOneWebCallThrowsUnexpectedly_TheTurnContinuesAndTheOtherStillReviews()
    {
        // A throw neither service maps (here from the handler itself) must become that call's refusal: the segment's
        // Task.WhenAll would otherwise fault the whole turn, taking the search review and the question down with it.
        using var server = new WebReviewTestServer
        {
            PageFailure = new InvalidOperationException("stub parser blew up")
        };
        var (dispatcher, cards) = WebReviewDispatcher();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        using var model = new WebReviewScriptedChatClient();
        var runner = CreateRunner(RealPipelineFactory(model, stash),
            eventDispatcher: dispatcher,
            userQuestionAnswerStash: stash,
            webReviewRetriever: server.CreateRetriever());
        var package = RuntimePackageBuilder.Valid()
                                           .WithAllowedTool(WebFetchToolDefinition.ToolName, requiresApproval: true)
                                           .WithAllowedTool(WebSearchToolDefinition.ToolName, requiresApproval: true)
                                           .WithAllowedTool(AskUserTool.ToolName, requiresApproval: true)
                                           .Build();

        var runTask = RunAsync(runner, package);
        await ResolveWebCardAsync(runner, cards, expectedCount: 1, approved: true);
        await ResolveWebCardAsync(runner, cards, expectedCount: 2, approved: true);
        var review = await ResolveWebCardAsync(runner, cards, expectedCount: 3, approved: true);
        AssertEx.Equal(WebSearchToolDefinition.ToolName, review.ToolName, "the failed fetch has nothing to review, so the search review is the only one");
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(10));
        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = AssertEx.NotNull(question).RequestId,
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Which auth method?",
                    Selected = ["API key"],
                    Other = null
                }
            ]
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal("all done", model.FinalText);
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.FetchCallId], "request-failed");
        AssertEx.False(model.Results[WebReviewScriptedChatClient.FetchCallId].Contains("stub parser blew up", StringComparison.Ordinal),
            "the exception text is logged by type only and never handed to the model");
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.SearchCallId], WebReviewTestServer.SearchHitUrl);
        AssertEx.Contains(model.Results[WebReviewScriptedChatClient.AskCallId], "API key");
        AssertEx.Equal(expected: 3, cards.Count, "two consents and one review");
    }

    [Test]
    public async Task RunAsync_WhenAskUserArgumentsAreMalformed_NeverPromptsAndTheTurnStillCompletes()
    {
        // Nothing unvalidated may reach a human: ask_user is intercepted before ToolArgumentRepairAIFunction, so the
        // runner is the first guard. A bad call is answered to the MODEL, not shown to the operator.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var stash = new UserQuestionAnswerStash(TimeProvider.System);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1
                ? AskUserRequestUpdates(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["questions"] = Array.Empty<object>()
                })
                : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, userQuestionAnswerStash: stash);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build();

        await RunAsync(runner, package).WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.DidNotReceive().ReportUserQuestionAsync(Arg.Any<UserQuestionLifecyclePayload>());
        AssertEx.Equal(expected: 2, segment, "a malformed call must still resume the turn rather than fail it");
        AssertEx.True(stash.TryPop("call-ask-user", out var stashed));
        AssertEx.Contains(stashed, "\"answered\":false");
        AssertEx.Contains(stashed, UserQuestionResults.MalformedCallReason);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenAskUserCallHasABlankCallId_StillCompletesInsteadOfFaultingTheTurn()
    {
        // ResolveToolCallCardId resolves a blank CallId to the tool name (so the card key matches the streaming
        // lifecycle's), and a blank key would otherwise throw out of the stash and take the whole turn with it.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? BlankCallIdAskUserUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));
        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = AssertEx.NotNull(question).RequestId,
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Q?",
                    Selected = ["A"],
                    Other = null
                }
            ]
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task ResolveUserQuestionResult_WhenRequestIdIsUnmatched_DoesNotResumeTheParkedTurn()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithAllowedTool(AskUserTool.ToolName).Build());
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));

        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = $"unmatched-{Guid.NewGuid():N}",
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Q?",
                    Selected = ["A"],
                    Other = null
                }
            ]
        });

        AssertEx.False(runTask.IsCompleted, "a stale or unknown answer must be a no-op, never a resume");
        AssertEx.Equal(expected: 1, segment);

        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = AssertEx.NotNull(question).RequestId,
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Q?",
                    Selected = ["A"],
                    Other = null
                }
            ]
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the matching answer must resume the invocation");
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAQuestion_TheOperatorsThinkingTimeIsNotChargedToTheTurnDeadline()
    {
        // Regression guard: the invocation deadline (CancelAfter(InvocationTimeout)) must not run while a human is
        // thinking, or the operator gets "300 s minus whatever the model already spent" and the 10-minute
        // MaxPendingToolCallAge cap is dead code. Here the turn budget is 1 s and the operator takes ~2 s — without the
        // re-arm the turn dies as a Timeout before the answer can land.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        UserQuestionLifecyclePayload? question = null;
        dispatcher.ReportUserQuestionAsync(Arg.Do<UserQuestionLifecyclePayload>(payload => question = payload)).Returns(Task.CompletedTask);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? AskUserRequestUpdates(ValidAskUserArguments()) : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 1).WithAllowedTool(AskUserTool.ToolName).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => question is not null, TimeSpan.FromSeconds(5));
        // real-timer: the turn budget is a REAL 1 s deadline armed with CancellationTokenSource.CancelAfter inside the runner, not a
        // TimeProvider a fake can advance. Outliving it is the assertion, so the wait has to be real wall-clock time; 2 s
        // is that 1 s plus margin for a contended runner, and the park is asserted immediately after it.
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "the turn must still be parked after the (unextended) invocation deadline would have fired");
        runner.ResolveUserQuestionResult(new UserQuestionAnsweredEvent
        {
            RequestId = AssertEx.NotNull(question).RequestId,
            Answers =
            [
                new UserQuestionAnswer
                {
                    Question = "Q?",
                    Selected = ["A"],
                    Other = null
                }
            ]
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAToolApproval_TheOperatorsThinkingTimeIsNotChargedToTheTurnDeadline()
    {
        // The same deadline re-arm applies to the shipping tool-approval round-trip — a deliberate, separately reviewable
        // behavior change to an existing feature, so it gets its own regression test.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        // real-timer: the same real CancelAfter deadline as the question park above: 1 s of turn budget has to actually elapse before
        // "still parked" means anything, and no fake clock reaches it.
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "an operator weighing an approval must not be pre-empted by the model's own turn budget");
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAToolApprovalWhileAttached_KeepsTheFullParkBudget()
    {
        // Pins today's behavior for the case the disconnect-grace work must NOT change: a browser is watching, so the
        // park still gets MaxPendingToolCallAge on top of the turn budget and the 1 s turn budget cannot pre-empt it.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var invocationId = Guid.NewGuid();
        var tracker = CreateAttachmentTracker();
        using var attachment = tracker.Attach(invocationId);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, attachmentTracker: tracker);
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        // real-timer: the same real CancelAfter deadline: the attached park's full budget is only proved by outliving the 1 s turn
        // budget in real time.
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "an attached operator weighing an approval must keep the full park budget");
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenParkedOnAToolApprovalWhileDetached_FallsBackToThePlainInvocationTimeout()
    {
        // A browser that disconnected while the approval card was on screen must not buy the run
        // MaxPendingToolCallAge + InvocationTimeout (~15 min) PER PARK, holding the llama-server lease the whole time,
        // waiting for an answer that can no longer arrive. Detached, the park gets only the turn budget.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var invocationId = Guid.NewGuid();
        var tracker = CreateAttachmentTracker();

        // Attached, then gone: exactly the "was watching, closed the tab" state. A run that NEVER attached is a
        // different case (scheduled/platform runs) and is covered by InvocationAttachmentTrackerTests.
        tracker.Attach(invocationId).Dispose();

        var runner = CreateRunner(CreateFactory(_ => ApprovalRequestUpdates()), eventDispatcher: dispatcher, attachmentTracker: tracker);
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var runTask = RunAsync(runner, package);

        // Nobody answers. The 1 s turn budget — not the 5 min park cap — is what ends this.
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        await dispatcher.Received(requiredNumberOfCalls: 1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenAClientReAttachesDuringAPark_RestoresTheFullParkBudget()
    {
        // The reload case. The park started detached (short budget); the operator reloads mid-park, and from that moment
        // the turn must get the full MaxPendingToolCallAge back — otherwise a reload inherits whatever the detached park
        // left behind and the answer arrives too late.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var invocationId = Guid.NewGuid();
        var tracker = CreateAttachmentTracker();
        tracker.Attach(invocationId).Dispose();

        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? ApprovalRequestUpdates() : CreateUpdates("done");
        });
        var runner = CreateRunner(factory, eventDispatcher: dispatcher, attachmentTracker: tracker);
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithTimeout(invocationSeconds: 1).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));

        // The reload lands well inside the detached park's 1 s budget and re-arms the deadline via AttachmentChanged.
        using var reattached = tracker.Attach(invocationId);
        // real-timer: the same real CancelAfter deadline: the re-armed budget is only proved by outliving the detached park's 1 s
        // budget in real time.
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertEx.False(runTask.IsCompleted, "the re-attached park must get the full budget back from the moment of re-attach");
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, segment);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task CancelDetached_ClassifiesTheTurnAsCancelledNotTimedOut()
    {
        // The reaper's cancel must look like an abandoned turn, not a node timeout: the row terminalizes Cancelled with
        // no error text, exactly as a user stop does.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();
        var runner = CreateRunner(CreateFactory(cancellationToken => WaitForCancellation(started, cancellationToken)), eventDispatcher: dispatcher);

        var runTask = RunAsync(runner, package);
        await started.Task;
        runner.CancelDetached(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(requiredNumberOfCalls: 1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.Cancelled);
    }

    [Test]
    public async Task RunAsync_WhenTwoToolApprovalsInOneSegment_AnswersBothOnResume()
    {
        // A parallel-tool-call turn surfaces TWO approval requests in one segment. The runner must present and
        // answer BOTH — the scalar this replaced kept only the last, so the first request dangled unanswered forever and
        // its tool call never executed. On resume, the folded history must carry a ToolApprovalResponseContent for EACH.
        IReadOnlyList<ChatMessage>? resumeMessages = null;
        var segment = 0;
        var factory = CreateMessageCapturingFactory(_ =>
            {
                segment++;
                return segment == 1 ? TwoApprovalRequestUpdates() : CreateUpdates("done");
            },
            messages => resumeMessages = messages);
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());

        // The transport presents approvals one at a time, so answer each as it arrives (present-each-in-turn).
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[0].RequestId,
            Approved = true
        });
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "the runner must resume only after BOTH approvals resolve");
        var responses = AssertEx.NotNull(resumeMessages)
                                .SelectMany(static message => message.Contents)
                                .OfType<ToolApprovalResponseContent>()
                                .ToList();
        AssertEx.Equal(expected: 2, responses.Count, "both approval requests must receive a ToolApprovalResponseContent on resume");
    }

    [Test]
    public async Task RunAsync_WhenApprovalReEmittedWithoutCallId_PresentedOnce()
    {
        // Hardening: a CallId-less approval re-emitted across streamed chunks must dedup on its Id and be
        // presented exactly ONCE — a blank CallId must never bypass dedup (that would prompt N times for one call and
        // dangle N-1 ambiguous responses).
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1 ? BlankCallIdApprovalUpdates() : CreateUpdates("done");
        });
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);
        var invocationId = Guid.NewGuid();

        var runTask = RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithAllowedTool("run_in_agent_home", requiresApproval: true).Build());

        // The whole segment (both chunks) drains before approvals are presented, so a bypassed dedup would already have
        // enqueued two; wait for the single presentation, resolve it, and confirm no second one follows.
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[0].RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 1, approvals.Count, "a CallId-less approval re-emitted across chunks must be presented exactly once");
        AssertEx.Equal(expected: 2, segment, "the run resumes after the single approval resolves");
    }

    [Test]
    public async Task RunAsync_WhenADifferentSkillResourceIsRead_TheSessionApprovalNoLongerApplies()
    {
        var conversationId = Guid.NewGuid();
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment switch
            {
                1 => SkillApprovalRequestUpdates(ReadSkillResourceToolName, SkillName, "reference.md"),
                3 => SkillApprovalRequestUpdates(ReadSkillResourceToolName, SkillName, "secrets.md"),
                _ => CreateUpdates("done")
            };
        });
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "one approval must cover ONE resource, not every resource the skill carries");
    }

    [Test]
    public async Task RunAsync_WhenFixedCustomToolApprovedForSession_SuppressesTheNextPrompt()
    {
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(CustomToolApprovalFactory(CustomToolName), eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, CustomToolPackage(conversationId, isFixed: true).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        // A SECOND turn in the SAME conversation, same Fixed custom tool at the same version: the memo answers it.
        await RunAsync(runner, CustomToolPackage(conversationId, isFixed: true).Build());

        AssertEx.Equal(expected: 1, approvals.Count, "a session-scoped approval on a Fixed custom tool must not prompt again in the same conversation");
    }

    [Test]
    public async Task RunAsync_WhenParameterizedCustomToolApprovedForSession_PromptsAgain()
    {
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(CustomToolApprovalFactory(CustomToolName), eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, CustomToolPackage(conversationId, isFixed: false).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        // A Parameterized custom tool is once-or-deny only: a session approval must NOT be remembered, so the next turn
        // re-prompts even though the operator clicked "approve for session".
        var secondTurn = RunAsync(runner, CustomToolPackage(conversationId, isFixed: false).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "a Parameterized custom tool must never be session-approvable — one click must not grant open-ended model-chosen execution");
    }

    [Test]
    public async Task RunAsync_WhenSkillApprovalIsDeniedForTheSession_PromptsAgain()
    {
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(SkillApprovalFactory(LoadSkillToolName, SkillName), eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = false
        }, ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = false
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "a DENY must never be remembered, whatever scope the operator sent");
    }

    [Test]
    public async Task RunAsync_WhenTheCustomToolVersionChanges_TheSessionApprovalNoLongerApplies()
    {
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(CustomToolApprovalFactory(CustomToolName), eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, CustomToolPackage(conversationId, version: 1, isFixed: true).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        // The operator edited the custom tool mid-conversation: same name, new version. The memo is bound to the version.
        var secondTurn = RunAsync(runner, CustomToolPackage(conversationId, version: 2, isFixed: true).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "an edit that bumps the custom tool version must invalidate the memo and re-prompt");
    }

    [Test]
    public async Task RunAsync_WhenTheNodeDisablesSessionScope_TheSkillApprovalIsNotRemembered()
    {
        var conversationId = Guid.NewGuid();
        var alwaysPrompt = NodeToolApprovalPolicy.FromSettings(new NodeToolApprovalPolicySettings
        {
            DisableSkillSessionScope = true
        });
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(SkillApprovalFactory(LoadSkillToolName, SkillName), approvalPolicy: alwaysPrompt, eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "the operator's always-prompt switch must turn session scope off entirely");
    }

    [Test]
    public async Task RunAsync_WhenTheSkillIsImported_TheSessionApprovalIsNotRemembered()
    {
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(SkillApprovalFactory(LoadSkillToolName, SkillName), eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId, imported: true).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        var secondTurn = RunAsync(runner, SkillPackage(conversationId, imported: true).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "third-party skill names are attacker-chosen; a durable approval on one must not be available");
    }

    [Test]
    public async Task RunAsync_WhenTheSkillVersionChanges_TheSessionApprovalNoLongerApplies()
    {
        var conversationId = Guid.NewGuid();
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var runner = CreateRunner(SkillApprovalFactory(LoadSkillToolName, SkillName), eventDispatcher: dispatcher);

        var firstTurn = RunAsync(runner, SkillPackage(conversationId).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await firstTurn;

        // The operator edited the skill (or an import Replaced it) mid-conversation: same name, new content, new version.
        var secondTurn = RunAsync(runner, SkillPackage(conversationId, version: 2).Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 2, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals[1].RequestId,
            Approved = true
        });
        await secondTurn;

        AssertEx.Equal(expected: 2, approvals.Count, "a content change must invalidate the memo — the approval is bound to the version the operator saw");
    }
}
