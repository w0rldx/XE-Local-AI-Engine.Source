namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Invocation.InvocationRunnerTestSupport;

[Category(TestCategories.Unit)]
public sealed class InvocationRunnerToolOfferTests
{
    [Test]
    public async Task RunAsync_WithTheNodeSettingUnset_LeavesTheRelevanceScopeInactiveAndNeverReadsTheCoreSet()
    {
        // The byte-identical pin for the shipped default. The settings read itself is unconditional (it is the LEFT
        // operand of the &&), so a read-count assertion would be wrong; the CORE SET is what the runner touches only
        // when the decision came out active, which makes it the honest negative observable.
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        var runner = CreateRunner(toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        coreSet.DidNotReceive().GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenTheNodeSettingIsOn_SeedsAnActiveRelevanceScopeFromTheStoredValue()
    {
        // The behavior change this plan exists for: the decision now comes from the node setting, read per turn.
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        coreSet.GetCoreToolNames().Returns(new HashSet<string>(StringComparer.Ordinal));
        var runner = CreateRunner(toolRelevanceRead: static _ => Task.FromResult(true),
            toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        coreSet.Received(1).GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenTheAgentOptsOut_KeepsTheScopeInactiveEvenWithTheNodeSettingOn()
    {
        // Guards the operand ORDER of `enabled && !package.DisableToolRelevanceFilter` against a later edit: the
        // per-agent opt-out must still win over the global switch.
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        var runner = CreateRunner(toolRelevanceRead: static _ => Task.FromResult(true),
            toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build() with
        {
            DisableToolRelevanceFilter = true
        });

        coreSet.DidNotReceive().GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenTheStoredValueChangesBetweenTurns_ThePreviouslyBuiltRunnerPicksItUp()
    {
        // ONE runner, two turns. Two separately constructed runners would pass even if the value were captured at
        // construction, which is exactly the no-restart bug this rules out.
        var enabled = false;
        var coreSet = Substitute.For<IToolRelevanceCoreSet>();
        coreSet.GetCoreToolNames().Returns(new HashSet<string>(StringComparer.Ordinal));
        var runner = CreateRunner(
            // ReSharper disable once AccessToModifiedClosure - reading the CURRENT value per turn is the point.
            toolRelevanceRead: _ => Task.FromResult(enabled),
            toolRelevanceCoreSet: coreSet);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());
        coreSet.DidNotReceive().GetCoreToolNames();

        enabled = true;
        await RunAsync(runner, RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build());

        coreSet.Received(1).GetCoreToolNames();
    }

    [Test]
    public async Task RunAsync_WhenTheRelevanceHopHeldToolsBack_EmitsOneToolsFilteredNoticeAfterTheFirstAssistantText()
    {
        // The drain the hop cannot do itself: it leaves the pair on the ambient scope several awaited frames below the
        // runner, and the runner turns it into the one counts-only sentence at the end of the FIRST segment — after
        // the assistant text, exactly as HistoryTruncated does.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var chunksReportedWhenTheNoticeFired = -1;
        var reportedChunks = 0;
        dispatcher.When(static call => call.ReportInvocationStreamChunkAsync(Arg.Any<Guid>(), Arg.Any<string>()))
                  .Do(_ => reportedChunks++);
        dispatcher.When(static call => call.ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.ToolsFiltered)))
                  .Do(_ => chunksReportedWhenTheNoticeFired = reportedChunks);
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: ToolsFilteredUpdates(hidden: 5, total: 12, "Hello"),
            toolRelevanceRead: static _ => Task.FromResult(true));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        // The sentence is asserted verbatim: the ChatNoticeRow test renders whatever the server sends, so this is the
        // only place the wording is pinned.
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.ToolsFiltered
            && payload.Message == "5 of 12 tools were held back from this turn to save context; the assistant can list and use them by calling list_tools."));
        AssertEx.True(chunksReportedWhenTheNoticeFired >= 1, "The notice must follow the first assistant text, never precede it.");
    }

    [Test]
    public async Task RunAsync_WhenNoToolWasHeldBack_EmitsNoToolsFilteredNotice()
    {
        // Both silent shapes: the shipped default (the filter never engages) and an ACTIVE filter whose decision hid
        // nothing — "0 of N tools were held back" is a sentence no user should ever see.
        var shippedDefaultDispatcher = Substitute.For<IWorkerEventDispatcher>();
        await RunAsync(CreateRunner(eventDispatcher: shippedDefaultDispatcher, agentUpdates: CreateUpdates("Hello")),
            RuntimePackageBuilder.Valid().Build());

        var nothingHiddenDispatcher = Substitute.For<IWorkerEventDispatcher>();
        await RunAsync(CreateRunner(eventDispatcher: nothingHiddenDispatcher,
                agentUpdates: ToolsFilteredUpdates(hidden: 0, total: 12, "Hello"),
                toolRelevanceRead: static _ => Task.FromResult(true)),
            RuntimePackageBuilder.Valid().Build());

        foreach (var dispatcher in new[]
                 {
                     shippedDefaultDispatcher,
                     nothingHiddenDispatcher
                 })
        {
            await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.ToolsFiltered));
        }
    }

    [Test]
    public async Task RunAsync_WhenASecondSegmentFiltersAgain_KeepsTheNoticeToTheFirstSegmentsCounts()
    {
        // The drain is inside `if (isFirstSegment …)`, so an approval resume that rebinds the tool array and computes a
        // second decision must NOT post a second "tools were held back" line under output the user has already read.
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var segment = 0;
        var factory = CreateFactory(_ =>
        {
            segment++;
            return segment == 1
                ? ToolsFilteredApprovalRequestUpdates(hidden: 5, total: 12)
                : ToolsFilteredUpdates(hidden: 3, total: 9, "done");
        });
        var runner = CreateRunner(factory,
            eventDispatcher: dispatcher,
            toolRelevanceRead: static _ => Task.FromResult(true));
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("run_in_agent_home", requiresApproval: true).Build();

        var runTask = RunAsync(runner, package);
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await runTask;

        AssertEx.Equal(expected: 2, segment, "The resume segment must actually run, or this proves nothing.");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.ToolsFiltered
            && payload.Message.StartsWith("5 of 12 tools", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_OnACompletedTurn_ReportsTheBudgetsToolSchemaTokenEstimateBeforeTheTerminalReport()
    {
        // The columns are populated on the SHIPPED default (tool relevance off): the budget counts the schema either
        // way, and that is what makes the before/after measurable at all. Cumulative across rounds, maximum per round.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var order = new List<string>();
        dispatcher.When(static call => call.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())).Do(_ => order.Add("estimate"));
        dispatcher.When(static call => call.ReportInvocationCompletedAsync(Arg.Any<Guid>(),
                      Arg.Any<int?>(),
                      Arg.Any<int?>(),
                      Arg.Any<int?>(),
                      Arg.Any<int?>(),
                      Arg.Any<long?>(),
                      Arg.Any<string?>(),
                      Arg.Any<InvocationThroughput?>()))
                  .Do(_ => order.Add("completed"));
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ToolSchemaBudgetedUpdates(640, 300));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportToolSchemaTokensAsync(package.InvocationId, 940L, 640);
        AssertEx.True(order.SequenceEqual(["estimate", "completed"], StringComparer.Ordinal),
            "The estimate must reach the invocation state BEFORE the terminal report, or the envelope row is written without it.");
    }

    [Test]
    public async Task RunAsync_WithAReservedOutputOverride_SeedsTheProviderBudgetWithItAsTheFloorWhileChatKeepsTheNodeFloor()
    {
        // The inner provider-round budget reserves its own floor. A benchmark's exact reservation must reach it too, or the round
        // refuses the window the outer budget just accepted — the same seam the Development coder scopes its round budget through.
        var benchmarkFloors = new List<int>();
        var chatFloors = new List<int>();

        await RunAsync(CreateRunner(agentUpdates: ReservedFloorCapturingUpdates(benchmarkFloors)), RuntimePackageBuilder.Valid().Build() with
        {
            ReservedOutputTokensOverride = 512
        });
        await RunAsync(CreateRunner(agentUpdates: ReservedFloorCapturingUpdates(chatFloors)), RuntimePackageBuilder.Valid().Build());

        AssertEx.Equal(expected: 512, benchmarkFloors.Single());
        AssertEx.Equal(new ProviderCallBudgetOptions().ReservedOutputTokenFloor, chatFloors.Single());
    }

    [Test]
    public async Task RunAsync_WhenTheTurnIsCancelled_StillReportsTheToolSchemaTokenEstimate()
    {
        // The easiest path to lose, and the most interesting one to keep: a turn that was stopped is exactly where an
        // operator asks what the tool schema was costing.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var order = new List<string>();
        dispatcher.When(static call => call.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())).Do(_ => order.Add("estimate"));
        dispatcher.When(static call => call.ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>())).Do(_ => order.Add("terminal"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(CreateFactory(cancellationToken => ToolSchemaBudgetedThenParkedUpdates(640, started, cancellationToken)), eventDispatcher: dispatcher);
        var package = RuntimePackageBuilder.Valid().WithTimeout().Build();

        var runTask = RunAsync(runner, package);
        await started.Task;
        runner.Cancel(package.InvocationId);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        await dispatcher.Received(1).ReportToolSchemaTokensAsync(package.InvocationId, 640L, 640);
        AssertEx.True(order.SequenceEqual(["estimate", "terminal"], StringComparer.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenTheTurnFails_StillReportsTheToolSchemaTokenEstimate()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var order = new List<string>();
        dispatcher.When(static call => call.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())).Do(_ => order.Add("estimate"));
        dispatcher.When(static call => call.ReportInvocationFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<FailureCategory>())).Do(_ => order.Add("terminal"));
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ToolSchemaBudgetedThenThrowingUpdates(640));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportToolSchemaTokensAsync(package.InvocationId, 640L, 640);
        AssertEx.True(order.SequenceEqual(["estimate", "terminal"], StringComparer.Ordinal));
    }

    [Test]
    public async Task RunAsync_WhenTheEstimateReportThrows_StillCompletesTheTurn()
    {
        // Telemetry never decides an outcome. The report runs immediately before the terminal report, so an unguarded
        // throw on the completed path would fall into the catch below it and turn a finished turn into a failed one.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportToolSchemaTokensAsync(Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<int?>())
                  .Returns<Task>(static _ => throw new InvalidOperationException("the estimate seam broke"));
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CreateUpdates("Hello"));
        var package = RuntimePackageBuilder.Valid().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportInvocationCompletedAsync(package.InvocationId,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<InvocationThroughput?>());
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
    }

    [Test]
    public async Task RunAsync_WhenTheProviderRepeatsTheSameToolCall_EmitsOneRequestedEventPerDistinctCall()
    {
        // A re-emitted FunctionCallContent must not pay a fresh JsonSerializer.Serialize + dispatch + SignalR frame every
        // time. Downstream absorbs the duplicates (the frontend reducer keys tool cards on the call id), but each repeat
        // would also displace a real event from InvocationResumeRegistry's capped tool history.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: RepeatedToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        // Three emissions of call-1 (the same instance twice, then an equal-but-distinct arguments dictionary — the two
        // shapes a provider can re-emit) collapse to one event.
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Requested
            && payload.ToolCallId == "call-1"
            && payload.Arguments != null
            && payload.Arguments.Contains("README.md", StringComparison.Ordinal)));

        // A genuinely distinct call still reports — the dedup is per call id, not per tool.
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Requested
            && payload.ToolCallId == "call-2"));

        // The Completed side still resolves the tool name from what the Requested side recorded.
        await dispatcher.Received(1).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Completed
            && payload.ToolCallId == "call-1"
            && payload.ToolName == "test-tool"));
    }

    [Test]
    public async Task RunAsync_WhenARepeatedToolCallChangesItsArguments_EmitsBothRequestedEvents()
    {
        // The guard must never swallow a payload change: the second event is genuinely different on the wire, and the
        // frontend reducer takes the newest arguments for a card.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: ChangedArgumentsToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        await dispatcher.Received(2).ReportToolCallLifecycleAsync(Arg.Is<ToolCallLifecyclePayload>(payload =>
            payload.Phase == ToolCallLifecyclePhase.Requested
            && payload.ToolCallId == "call-1"));
    }

    [Test]
    public async Task RunAsync_WhenTheProviderStreamsTwoSameNameCallsWithABlankCallId_PairsEachResultWithItsOwnCall()
    {
        // Microsoft.Extensions.AI rejects a NULL CallId in both content constructors (10.9.0), so the id-less shape a
        // provider can actually produce is the empty string — and both halves of the call then carried it. Every
        // consumer that correlates a call with its result drops a blank id (NodeChatPartAccumulator refuses one
        // outright), so an id-less call was recorded nowhere and a caller-managed continuation never replayed it. Two
        // such calls to one tool also collapsed onto a single card.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var lifecycle = new List<ToolCallLifecyclePayload>();
        dispatcher.ReportToolCallLifecycleAsync(Arg.Do<ToolCallLifecyclePayload>(lifecycle.Add)).Returns(Task.CompletedTask);
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: CallIdLessToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        var requested = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Requested).ToList();
        var completed = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Completed).ToList();

        AssertEx.Equal(expected: 2, requested.Count, "Two calls to one tool are two calls, not one card overwritten by the second.");
        AssertEx.Equal(expected: 2, completed.Count);
        AssertEx.NotEqual(requested[0].ToolCallId, requested[1].ToolCallId, "A second call to the same tool must take its own surrogate id.");
        AssertEx.True(completed.TrueForAll(static payload => !string.IsNullOrEmpty(payload.ToolCallId)),
            "An empty id is dropped by every consumer that correlates a result with its call.");

        // Same ids, in call order: results arrive in call order for a provider that emits none.
        AssertEx.Equal(requested[0].ToolCallId, completed[0].ToolCallId);
        AssertEx.Equal(requested[1].ToolCallId, completed[1].ToolCallId);
        AssertEx.Equal("a.txt", completed[0].Result);
        AssertEx.Equal("b.txt", completed[1].Result);
        AssertEx.True(completed.TrueForAll(static payload => string.Equals(payload.ToolName, "test-tool", StringComparison.Ordinal)),
            "The Completed side still resolves the tool name from what the Requested side recorded.");
        AssertEx.Equal("test-tool", requested[0].ToolCallId, "The FIRST id-less call keeps the tool name, which is the id the approval card already resolves.");
    }

    [Test]
    public async Task RunAsync_WhenABlankCallIdToolIsCalledTwiceInSequence_DoesNotReuseTheClosedSurrogate()
    {
        // The sequential shape, with IDENTICAL arguments — the one that reads exactly like a streamed re-emission.
        // Reusing the finished call's key swallowed the second call outright here, and merged the first call's
        // arguments with the last result when the arguments differed. A surrogate is retired by its result.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var lifecycle = new List<ToolCallLifecyclePayload>();
        dispatcher.ReportToolCallLifecycleAsync(Arg.Do<ToolCallLifecyclePayload>(lifecycle.Add)).Returns(Task.CompletedTask);
        var runner = CreateRunner(eventDispatcher: dispatcher, agentUpdates: SequentialCallIdLessToolCallUpdates());
        var package = RuntimePackageBuilder.Valid().WithAllowedTool("test-tool").Build();

        await RunAsync(runner, package);

        var requested = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Requested).ToList();
        var completed = lifecycle.Where(static payload => payload.Phase == ToolCallLifecyclePhase.Completed).ToList();

        AssertEx.Equal(expected: 2, requested.Count, "The second call is a call, not a re-emission of the finished one.");
        AssertEx.Equal("test-tool", requested[0].ToolCallId, "The first id-less call keeps the tool name the approval card resolves.");
        AssertEx.Equal("test-tool#2", requested[1].ToolCallId, "A closed surrogate is retired, never reused.");

        AssertEx.Equal(expected: 2, completed.Count);
        AssertEx.Equal("test-tool", completed[0].ToolCallId);
        AssertEx.Equal("test-tool#2", completed[1].ToolCallId);
        AssertEx.Equal("first", completed[0].Result);
        AssertEx.Equal("second", completed[1].Result);
    }
}
