namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     The approval rules that are cheap to state and expensive to get wrong, exercised on the coordinator alone rather
///     than through the whole invocation runner: the two-guard ORDER (an unattended run is refused BEFORE the session
///     memo is consulted, so a populated memo can never satisfy an approval nobody can see), the fail-closed
///     256-entry memo cap, and the per-segment duplicate-request dedup.
/// </summary>
public sealed class ToolApprovalCoordinatorTests
{
#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI; the same scoped suppression the provider call sites use.
    private const string LoadSkillToolName = AgentSkillsProvider.LoadSkillToolName;
#pragma warning restore MAAI001

    private const string SkillName = "demo";

    private static readonly Guid SkillId = Guid.Parse("2f2f9a3e-0d1a-4c9a-9d9c-6f6f0a2b7c11");

    [Test]
    public async Task RequestToolApprovalAsync_WhenUnattended_RefusesBeforeTheSessionMemoIsConsulted()
    {
        var sender = new MockHubMessageSender();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var coordinator = CreateCoordinator(sender, auditRecorder: auditRecorder);
        var conversationId = Guid.NewGuid();

        // Grant a session-scoped approval for exactly this skill tool, skill and version, so the memo WOULD answer the
        // second request if it were ever reached.
        await GrantSessionApprovalAsync(coordinator, sender, SkillPackage(conversationId));

        var unattended = SkillPackage(conversationId).AsUnattended().Build();
        var exception = await AssertEx.ThrowsAsync<ApprovalUnavailableException>(() =>
            coordinator.RequestToolApprovalAsync(unattended, SkillApprovalRequest(), static _ => { }, CancellationToken.None));

        AssertEx.Contains(exception.Message, "unattended", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 1, sender.SentApprovals.Count, "the unattended run must not raise a card");

        // The decisive assertion: the refusal is audited, and the memo hit that would have approved it never is. If the
        // guards were inverted the second call would audit "session-scope auto-approve" and return true.
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               LoadSkillToolName,
                               Arg.Any<ToolCategory>(),
                               "unattended-unavailable",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
        await auditRecorder.DidNotReceive()
                           .RecordAsync(Arg.Any<Guid?>(),
                               Arg.Any<string>(),
                               Arg.Any<ToolCategory>(),
                               "session-scope auto-approve",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>())
                           .ConfigureAwait(false);
    }

    [Test]
    public async Task RequestToolApprovalAsync_WhenTheSessionMemoIsFull_FailsClosedAndPromptsAgain()
    {
        var sender = new MockHubMessageSender();
        var coordinator = CreateCoordinator(sender);
        var conversationId = Guid.NewGuid();

        // 256 distinct memo keys — same conversation and tool, one per skill VERSION — fill the cap exactly.
        for (var version = 1; version <= 256; version++)
        {
            await GrantSessionApprovalAsync(coordinator, sender, SkillPackage(conversationId, version));
        }

        AssertEx.Equal(expected: 256, sender.SentApprovals.Count);

        // The 257th grant is refused by the cap: the operator's approval still applies to THIS call, but nothing is
        // remembered, so the very next request for it must prompt again.
        await GrantSessionApprovalAsync(coordinator, sender, SkillPackage(conversationId, version: 257));
        await GrantSessionApprovalAsync(coordinator, sender, SkillPackage(conversationId, version: 257));
        AssertEx.Equal(expected: 258, sender.SentApprovals.Count, "an overflowed memo must fail closed and re-prompt");

        // An entry that made it in before the cap is still honoured — overflow only ever ADDS prompts.
        var remembered = await coordinator.RequestToolApprovalAsync(SkillPackage(conversationId, version: 1).Build(),
            SkillApprovalRequest(),
            static _ => { },
            CancellationToken.None);

        AssertEx.True(remembered);
        AssertEx.Equal(expected: 258, sender.SentApprovals.Count, "a remembered approval must not prompt");
    }

    [Test]
    public async Task RequestToolApprovalAsync_OnALoopbackTurn_DispatchesLocallyWithoutTheHub()
    {
        // The ordinary desktop case: an unpaired standalone node whose hub sender throws on every send. The approval
        // must still reach the local chat stream and park the turn — sending to the hub first failed the whole turn
        // before the card was ever rendered.
        var sender = new MockHubMessageSender();
        sender.ThrowOnNextSend(new InvalidOperationException("Worker hub connection is not active. Cannot send 'ApprovalRequested'."));

        ApprovalRequestPayload? dispatchedApproval = null;
        ApprovalLifecyclePayload? dispatchedLifecycle = null;
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportApprovalRequestedAsync(Arg.Do<ApprovalRequestPayload>(payload => dispatchedApproval = payload)).Returns(Task.CompletedTask);
        dispatcher.ReportApprovalLifecycleAsync(Arg.Do<ApprovalLifecyclePayload>(payload => dispatchedLifecycle = payload)).Returns(Task.CompletedTask);

        var coordinator = CreateCoordinator(sender, dispatcher: dispatcher);
        var loopback = RuntimePackageBuilder.Valid()
                                            .WithRequestedCapability(LocalChatLoopbackDefaults.RequestedCapability)
                                            .Build();

        var pending = coordinator.RequestToolApprovalAsync(loopback, ToolApprovalRequest(), static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => dispatchedLifecycle is not null, TimeSpan.FromSeconds(5));

        AssertEx.Equal(expected: 0, sender.SentApprovals.Count, "a loopback turn must not touch the worker hub");
        AssertEx.False(pending.IsCompleted, "the turn parks on the approval card");

        // The loopback resolve endpoint answers the card the local dispatch rendered, and the turn continues.
        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent(AssertEx.NotNull(dispatchedApproval).RequestId, Approved: true));
        AssertEx.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task RequestToolApprovalAsync_OnAHubBoundTurn_StillSendsToTheHub()
    {
        // The paired case is unchanged: a package without the loopback capability still raises the card on the hub.
        var sender = new MockHubMessageSender();
        var coordinator = CreateCoordinator(sender);

        var pending = coordinator.RequestToolApprovalAsync(RuntimePackageBuilder.Valid().Build(),
            ToolApprovalRequest(),
            static _ => { },
            CancellationToken.None);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));
        AssertEx.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task ResolveApprovalResult_ReleasesACallTheApiBridgeRegistered()
    {
        // The two collaborators must share ONE pending-call dictionary: the bridge parks the call, the coordinator's
        // resolve (fed by the hub/loopback endpoint) releases it. Two copies would strand the turn until its timeout.
        await Task.CompletedTask;

        var sender = new MockHubMessageSender();
        var registry = new PendingToolCallRegistry();
        var coordinator = CreateCoordinator(sender, registry);
        var bridge = new ApiToolCallBridge(new Lazy<IHubMessageSender>(() => sender),
            new Lazy<IWorkerEventDispatcher>(() => Substitute.For<IWorkerEventDispatcher>()),
            registry,
            StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).Build());

        var call = bridge.ExecuteApiToolCallAsync(Guid.NewGuid(), "test-tool", "{}", requiresApproval: true, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count == 1, TimeSpan.FromSeconds(5));

        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals.Single().RequestId, Approved: true));

        await AssertEx.EventuallyAsync(() => sender.SentToolCalls.Count == 1, TimeSpan.FromSeconds(5));
        AssertEx.False(call.IsCompleted, "the call is still waiting for its RESULT, not its approval");
    }

    [Test]
    public async Task IsDuplicatePendingApproval_DedupesOnTheStableKeyAndFallsBackToReferenceIdentity()
    {
        await Task.CompletedTask;

        var pending = new List<ToolApprovalRequestContent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // A CallId is the preferred key: the same call re-emitted across streamed chunks is captured once.
        var withCallId = new ToolApprovalRequestContent("request-1", new FunctionCallContent("call-1", "tool", null));
        AssertEx.False(ToolApprovalCoordinator.IsDuplicatePendingApproval(withCallId, pending, seen));
        pending.Add(withCallId);
        AssertEx.True(ToolApprovalCoordinator.IsDuplicatePendingApproval(withCallId, pending, seen));

        // A BLANK CallId must not bypass dedup: it falls through to the approval's own RequestId.
        var blankCallId = new ToolApprovalRequestContent("request-2", new FunctionCallContent(string.Empty, "tool", null));
        AssertEx.False(ToolApprovalCoordinator.IsDuplicatePendingApproval(blankCallId, pending, seen));
        pending.Add(blankCallId);
        AssertEx.True(ToolApprovalCoordinator.IsDuplicatePendingApproval(blankCallId, pending, seen));

        // Two different calls with distinct RequestIds are NOT duplicates of each other, so a segment carrying several
        // blank-CallId approvals still enqueues them all.
        var otherBlankCallId = new ToolApprovalRequestContent("request-3", new FunctionCallContent(string.Empty, "tool", null));
        AssertEx.False(ToolApprovalCoordinator.IsDuplicatePendingApproval(otherBlankCallId, pending, seen));

        // The reference-identity fallback below the two key branches is unreachable through this API: MEAI's
        // InputRequestContent constructor rejects a blank RequestId, so a stable key always exists. It stays as the
        // defensive floor for a future content type that does not carry one.
    }

    // Raises one approval and answers it with ApprovalScope.Session. RequestToolApprovalAsync runs synchronously up to
    // the completion await (every send/report the coordinator makes returns a completed task), so the request id is on
    // the sender before the resolve — no polling needed, and 256 iterations stay fast.
    private static async Task GrantSessionApprovalAsync(ToolApprovalCoordinator coordinator,
        MockHubMessageSender sender,
        RuntimePackageBuilder packageBuilder)
    {
        var pending = coordinator.RequestToolApprovalAsync(packageBuilder.Build(), SkillApprovalRequest(), static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => sender.SentApprovals.Count > 0 && !pending.IsCompleted, TimeSpan.FromSeconds(5));
        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent(sender.SentApprovals[^1].RequestId, Approved: true), ApprovalScope.Session);
        AssertEx.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // A plain, memo-INELIGIBLE approval request (not a skill tool), so the session memo never short-circuits the
    // request under test.
    private static ToolApprovalRequestContent ToolApprovalRequest()
    {
        return new ToolApprovalRequestContent($"approval-{Guid.NewGuid():N}", new FunctionCallContent($"call-{Guid.NewGuid():N}", "GetCurrentTime"));
    }

    private static ToolApprovalRequestContent SkillApprovalRequest()
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["skillName"] = SkillName
        };

        return new ToolApprovalRequestContent($"approval-{Guid.NewGuid():N}", new FunctionCallContent($"call-{Guid.NewGuid():N}", LoadSkillToolName, arguments));
    }

    // The skill tools are never in the package's tool OFFER (they reach the model through MAF's context provider), so
    // only the resolved-skill set matters here — it is what supplies the VERSION the memo key binds to.
    private static RuntimePackageBuilder SkillPackage(Guid conversationId, int version = 1)
    {
        return RuntimePackageBuilder.Valid()
                                    .WithConversationId(conversationId)
                                    .WithSkills(new ResolvedSkill(SkillId, SkillName, "A skill.", "Skill body.", version, IsImported: false));
    }

    private static ToolApprovalCoordinator CreateCoordinator(MockHubMessageSender sender,
        PendingToolCallRegistry? registry = null,
        IToolApprovalAuditRecorder? auditRecorder = null,
        IWorkerEventDispatcher? dispatcher = null)
    {
        return new ToolApprovalCoordinator(new Lazy<IHubMessageSender>(() => sender),
            new Lazy<IWorkerEventDispatcher>(() => dispatcher ?? Substitute.For<IWorkerEventDispatcher>()),
            registry ?? new PendingToolCallRegistry(),
            auditRecorder ?? Substitute.For<IToolApprovalAuditRecorder>(),
            NodeToolApprovalPolicy.FromSettings(settings: null),
            new UserQuestionAnswerStash(TimeProvider.System),
            StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).Build(),
            NullLogger<ToolApprovalCoordinator>.Instance);
    }
}
