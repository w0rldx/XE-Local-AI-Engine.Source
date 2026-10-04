namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.WebAccess;

/// <summary>
///     The approval rules that are cheap to state and expensive to get wrong, exercised on the coordinator alone rather
///     than through the whole invocation runner: the two-guard ORDER (an unattended run is refused BEFORE the session
///     memo is consulted, so a populated memo can never satisfy an approval nobody can see), the fail-closed
///     256-entry memo cap, and the per-segment duplicate-request dedup.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ToolApprovalCoordinatorTests
{
    private const string LoadSkillToolName = AgentSkillsProvider.LoadSkillToolName;

    private const string SkillName = "demo";

    private static readonly Guid SkillId = Guid.Parse("2f2f9a3e-0d1a-4c9a-9d9c-6f6f0a2b7c11");

    [Test]
    public async Task RequestToolApprovalAsync_WhenUnattended_RefusesBeforeTheSessionMemoIsConsulted()
    {
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var dispatcher = new RecordingApprovalDispatcher();
        var coordinator = CreateCoordinator(auditRecorder: auditRecorder, dispatcher: dispatcher);
        var conversationId = Guid.NewGuid();

        // Grant a session-scoped approval for exactly this skill tool, skill and version, so the memo WOULD answer the
        // second request if it were ever reached.
        await GrantSessionApprovalAsync(coordinator, dispatcher, SkillPackage(conversationId));

        var unattended = SkillPackage(conversationId).AsUnattended().Build();
        var exception = await AssertEx.ThrowsAsync<ApprovalUnavailableException>(() =>
            coordinator.RequestToolApprovalAsync(unattended, SkillApprovalRequest(), static _ => { }, CancellationToken.None));

        AssertEx.Contains(exception.Message, "unattended", StringComparison.OrdinalIgnoreCase);

        // The decisive assertion: the refusal is audited, and the memo hit that would have approved it never is. If the
        // guards were inverted the second call would audit "session-scope auto-approve" and return true.
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               LoadSkillToolName,
                               Arg.Any<ToolCategory>(),
                               "unattended-unavailable",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
        await auditRecorder.DidNotReceive()
                           .RecordAsync(Arg.Any<Guid?>(),
                               Arg.Any<string>(),
                               Arg.Any<ToolCategory>(),
                               "session-scope auto-approve",
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequestToolApprovalAsync_WhenTheSessionMemoIsFull_FailsClosedAndPromptsAgain()
    {
        var dispatcher = new RecordingApprovalDispatcher();
        var coordinator = CreateCoordinator(dispatcher: dispatcher);
        var conversationId = Guid.NewGuid();

        // 256 distinct memo keys — same conversation and tool, one per skill VERSION — fill the cap exactly.
        for (var version = 1; version <= 256; version++)
        {
            await GrantSessionApprovalAsync(coordinator, dispatcher, SkillPackage(conversationId, version));
        }


        // The 257th grant is refused by the cap: the operator's approval still applies to THIS call, but nothing is
        // remembered, so the very next request for it must prompt again.
        await GrantSessionApprovalAsync(coordinator, dispatcher, SkillPackage(conversationId, version: 257));
        await GrantSessionApprovalAsync(coordinator, dispatcher, SkillPackage(conversationId, version: 257));

        // An entry that made it in before the cap is still honoured — overflow only ever ADDS prompts.
        var remembered = await coordinator.RequestToolApprovalAsync(SkillPackage(conversationId, version: 1).Build(),
            SkillApprovalRequest(),
            static _ => { },
            CancellationToken.None);

        AssertEx.True(remembered);
    }

    [Test]
    public async Task RequestToolApprovalAsync_OnALoopbackTurn_DispatchesTheCardLocallyAndParksTheTurn()
    {
        // The approval must reach the local chat stream and park the turn: the card the operator answers is the
        // only way the run continues.
        ApprovalRequestPayload? dispatchedApproval = null;
        ApprovalLifecyclePayload? dispatchedLifecycle = null;
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportApprovalRequestedAsync(Arg.Do<ApprovalRequestPayload>(payload => dispatchedApproval = payload)).Returns(Task.CompletedTask);
        dispatcher.ReportApprovalLifecycleAsync(Arg.Do<ApprovalLifecyclePayload>(payload => dispatchedLifecycle = payload)).Returns(Task.CompletedTask);

        var coordinator = CreateCoordinator(dispatcher: dispatcher);
        var loopback = RuntimePackageBuilder.Valid()
                                            .Build();

        var pending = coordinator.RequestToolApprovalAsync(loopback, ToolApprovalRequest(), static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => dispatchedLifecycle is not null, TimeSpan.FromSeconds(5));

        AssertEx.False(pending.IsCompleted, "the turn parks on the approval card");

        // The loopback resolve endpoint answers the card the local dispatch rendered, and the turn continues.
        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = AssertEx.NotNull(dispatchedApproval).RequestId,
            Approved = true
        });
        AssertEx.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task RequestToolApprovalAsync_WhenNobodyAnswersWithinTheAge_ExpiresNamingTheToolAndAuditsTimeout()
    {
        // The waiter's own age cap, on the injected clock: the coordinator is built with a five-minute age.
        var timeProvider = new ManualTimeProvider();
        var registry = new PendingToolCallRegistry();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var dispatcher = new RecordingApprovalDispatcher();
        var coordinator = CreateCoordinator(registry, auditRecorder, dispatcher, timeProvider);

        var pending = coordinator.RequestToolApprovalAsync(RuntimePackageBuilder.Valid().Build(), ToolApprovalRequest(), static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => dispatcher.Approvals.Count == 1 && timeProvider.ArmedTimerCount > 0, TimeSpan.FromSeconds(5));
        var requestId = dispatcher.Approvals[0].RequestId;

        timeProvider.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var exception = await AssertEx.ThrowsAsync<ApprovalExpiredException>(() => pending);
        AssertEx.Contains(exception.Message, "approval for tool 'GetCurrentTime' expired", StringComparison.Ordinal);
        AssertEx.False(registry.Calls.ContainsKey(requestId), "an expired approval must leave the registry");
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               "GetCurrentTime",
                               Arg.Any<ToolCategory>(),
                               ApprovalDecisions.Timeout,
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequestToolApprovalAsync_CarriesTheSerializedCallArgumentsOnTheApprovalPrompt()
    {
        // The operator approves WHAT runs; the tool-call card carrying the arguments only arrives after the decision.
        ApprovalLifecyclePayload? dispatchedLifecycle = null;
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportApprovalLifecycleAsync(Arg.Do<ApprovalLifecyclePayload>(payload => dispatchedLifecycle = payload)).Returns(Task.CompletedTask);
        var coordinator = CreateCoordinator(dispatcher: dispatcher);
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = "echo hello"
        };
        var request = new ToolApprovalRequestContent("approval-args", new FunctionCallContent("call-args", "run_in_agent_home", arguments));

        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.RequestToolApprovalAsync(RuntimePackageBuilder.Valid().Build(), request, static _ => { }, cancellation.Token);
        await AssertEx.EventuallyAsync(() => dispatchedLifecycle is not null, TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => pending);

        var lifecycle = AssertEx.NotNull(dispatchedLifecycle);
        AssertEx.Equal("run_in_agent_home", lifecycle.ToolName);
        AssertEx.Equal(JsonSerializer.Serialize(arguments), lifecycle.Arguments);
    }

    [Test]
    public async Task CleanupStaleToolCalls_FaultsTheApprovalNobodyAnswered_AndLeavesAFreshOneResolvable()
    {
        // The stale sweep runs every ToolCallCleanupService tick against the registry the APPROVAL round-trip
        // populates — the only thing that registers a pending tool call now. Real coordinator, real bridge, one shared
        // registry and one clock, exactly as the DI graph wires them: a sweep that silently stopped removing would
        // park every unanswered turn on its full MaxPendingToolCallAge instead of releasing it.
        var timeProvider = new ManualTimeProvider();
        var registry = new PendingToolCallRegistry();
        var dispatcher = new RecordingApprovalDispatcher();
        var coordinator = CreateCoordinator(registry, dispatcher: dispatcher, timeProvider: timeProvider);
        var bridge = new ApiToolCallBridge(registry, timeProvider);

        var abandoned = coordinator.RequestToolApprovalAsync(RuntimePackageBuilder.Valid().Build(), ToolApprovalRequest(), static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => dispatcher.Approvals.Count == 1, TimeSpan.FromSeconds(5));
        var abandonedRequestId = dispatcher.Approvals[0].RequestId;

        // The control is raised AFTER the clock moves, so the same cutoff that condemns the first call spares it.
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        var fresh = coordinator.RequestToolApprovalAsync(RuntimePackageBuilder.Valid().Build(), ToolApprovalRequest(), static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => dispatcher.Approvals.Count == 2, TimeSpan.FromSeconds(5));
        var freshRequestId = dispatcher.Approvals[1].RequestId;

        bridge.CleanupStaleToolCalls(TimeSpan.FromMinutes(5));

        // Asserted before the faulted waiter's own finally can run, so removal is attributed to the sweep alone.
        AssertEx.False(registry.Calls.ContainsKey(abandonedRequestId), "the sweep must remove the call nothing will ever answer");
        AssertEx.True(registry.Calls.ContainsKey(freshRequestId), "a call younger than the cutoff must survive the sweep");

        // The expiry names the tool that waited, so the failed turn says which approval ran out rather than "timed out".
        var exception = await AssertEx.ThrowsAsync<ApprovalExpiredException>(() => abandoned);
        AssertEx.Contains(exception.Message, "approval for tool 'GetCurrentTime' expired", StringComparison.Ordinal);

        // A second sweep at the same instant is a no-op: nothing else is condemned and the survivor still resolves
        // normally through the operator's card.
        bridge.CleanupStaleToolCalls(TimeSpan.FromMinutes(5));
        AssertEx.True(registry.Calls.ContainsKey(freshRequestId), "a repeated sweep must not condemn a call it already spared");
        AssertEx.False(fresh.IsCompleted, "a repeated sweep must not release a call the operator has not answered");

        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = freshRequestId,
            Approved = true
        });
        AssertEx.True(await fresh.WaitAsync(TimeSpan.FromSeconds(5)));
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

    // Raises one approval and answers it with ApprovalScope.Session. The request id is read off the dispatcher the
    // coordinator reports the card to — the only place it is published now that there is no hub send.
    [Test]
    [Arguments(WebFetchToolDefinition.ToolName, true)]
    [Arguments(WebSearchToolDefinition.ToolName, true)]
    [Arguments(AskUserTool.ToolName, false)]
    [Arguments("GetCurrentTime", false)]
    public void IsWebReviewRequest_MatchesTheTwoWebToolsByName(string toolName, bool expected)
    {
        var request = new ToolApprovalRequestContent("approval-1", new FunctionCallContent("call-1", toolName));

        AssertEx.Equal(expected, ToolApprovalCoordinator.IsWebReviewRequest(request));
    }

    [Test]
    public async Task RequestWebReviewAsync_WhenAccepted_ShowsThePreviewWithoutSessionScope_AndHandsTheModelTheFencedResult()
    {
        using var server = new WebReviewTestServer();
        using var scope = WebReviewResultScope.BeginScope();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(auditRecorder: auditRecorder, dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());
        var request = WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = WebReviewTestServer.PageUrl
        });

        var pending = ReviewAsync(coordinator, RuntimePackageBuilder.Valid().Build(), request);
        await AssertEx.EventuallyAsync(() => !cards.IsEmpty, TimeSpan.FromSeconds(5));
        var card = cards.Single();
        var preview = AssertEx.NotNull(card.WebReview, "the card carries the retrieved content for the user to read");
        AssertEx.Equal(WebReviewTestServer.PageText, preview.Text);
        AssertEx.Equal(WebReviewTestServer.PageUrl, preview.Url);
        AssertEx.Equal(expected: false, card.SessionScopeEligible, "a review can never be approved for the session");
        AssertEx.False(pending.IsCompleted, "the turn parks on the review");

        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = card.RequestId,
            Approved = true
        }, ApprovalScope.Session);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await HandlerResultAsync(request);
        AssertEx.Contains(result, WebReviewTestServer.PageText);
        AssertEx.Contains(result, UntrustedContentFraming.UntrustedTrustLabel);
        await AssertAuditedAsync(auditRecorder, WebFetchToolDefinition.ToolName, ApprovalDecisions.Approve);

        // Session scope asked for, never honoured: the next review of the same tool parks again.
        var second = ReviewAsync(coordinator, RuntimePackageBuilder.Valid().Build(), WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = WebReviewTestServer.PageUrl
        }, "call-2"));
        await AssertEx.EventuallyAsync(() => cards.Count == 2, TimeSpan.FromSeconds(5));
        AssertEx.False(second.IsCompleted);
        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = cards.Last().RequestId,
            Approved = false
        });
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RequestWebReviewAsync_WhenRejected_TheModelGetsTheDeclineText_AndItIsAuditedAsDeny()
    {
        using var server = new WebReviewTestServer();
        using var scope = WebReviewResultScope.BeginScope();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(auditRecorder: auditRecorder, dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());
        var request = WebRequest(WebSearchToolDefinition.ToolName, new
        {
            query = "tidal power"
        });

        var pending = ReviewAsync(coordinator, RuntimePackageBuilder.Valid().Build(), request);
        await AssertEx.EventuallyAsync(() => !cards.IsEmpty, TimeSpan.FromSeconds(5));
        AssertEx.Equal(WebReviewTestServer.SearchHitUrl, AssertEx.NotNull(AssertEx.NotNull(cards.Single().WebReview).Results).Single().Url);
        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = cards.Single().RequestId,
            Approved = false
        });
        AssertEx.Equal("Rejected by user.", await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        var result = await HandlerResultAsync(request);
        AssertEx.Contains(result, ToolApprovalCoordinator.WebContentDeclinedMessage);
        AssertEx.False(result.Contains("Ignore previous instructions", StringComparison.Ordinal), "rejected content must never reach the model");
        await AssertAuditedAsync(auditRecorder, WebSearchToolDefinition.ToolName, ApprovalDecisions.Deny);
    }

    [Test]
    public async Task RequestWebReviewAsync_InAutoMode_NeverSurfacesACard()
    {
        using var server = new WebReviewTestServer();
        using var scope = WebReviewResultScope.BeginScope();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(auditRecorder: auditRecorder, dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());
        var request = WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = WebReviewTestServer.PageUrl
        });

        await ReviewAsync(coordinator, RuntimePackageBuilder.Valid().AutoAcceptingWebContent().Build(), request).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Empty(cards);
        await dispatcher.DidNotReceive().ReportApprovalRequestedAsync(Arg.Any<ApprovalRequestPayload>());
        AssertEx.Contains(await HandlerResultAsync(request), WebReviewTestServer.PageText);
        await AssertAuditedAsync(auditRecorder, WebFetchToolDefinition.ToolName, "web-content auto-accept");
    }

    [Test]
    public async Task RequestWebReviewAsync_WhenWebAccessIsTurnedOffWhileTheCardIsOpen_TheAcceptedContentIsRefused()
    {
        using var server = new WebReviewTestServer();
        using var scope = WebReviewResultScope.BeginScope();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var webAccessEnabled = true;
        var settings = StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).Build();
        settings.GetWebAccessEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => webAccessEnabled);
        var coordinator = CreateCoordinator(dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever(), runtimeSettings: settings);
        var request = WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = WebReviewTestServer.PageUrl
        });

        var pending = ReviewAsync(coordinator, RuntimePackageBuilder.Valid().Build(), request);
        await AssertEx.EventuallyAsync(() => !cards.IsEmpty, TimeSpan.FromSeconds(5));
        webAccessEnabled = false;
        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = cards.Single().RequestId,
            Approved = true
        });
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await HandlerResultAsync(request);
        AssertEx.Contains(result, "web-access-disabled");
        AssertEx.False(result.Contains(WebReviewTestServer.PageText, StringComparison.Ordinal), "content fetched before the switch went off must not land after it");
    }

    [Test]
    public async Task RequestWebReviewAsync_InAutoMode_WhenWebAccessIsOff_TheContentIsRefused()
    {
        // The retrieval already ran with the switch on; auto mode must still re-read it before the content lands.
        using var server = new WebReviewTestServer();
        using var scope = WebReviewResultScope.BeginScope();
        var coordinator = CreateCoordinator(webReviewRetriever: server.CreateRetriever(),
            runtimeSettings: StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).WithWebAccessEnabled(false).Build());
        var request = WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = WebReviewTestServer.PageUrl
        });

        await ReviewAsync(coordinator, RuntimePackageBuilder.Valid().AutoAcceptingWebContent().Build(), request).WaitAsync(TimeSpan.FromSeconds(5));

        var result = await HandlerResultAsync(request);
        AssertEx.Contains(result, "web-access-disabled");
        AssertEx.False(result.Contains(WebReviewTestServer.PageText, StringComparison.Ordinal));
    }

    [Test]
    public async Task RequestWebReviewAsync_WhenUnattended_FetchesNothing_AndTheModelIsToldNoOneCanReview()
    {
        using var server = new WebReviewTestServer();
        using var scope = WebReviewResultScope.BeginScope();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());
        var request = WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = WebReviewTestServer.PageUrl
        });

        await ReviewAsync(coordinator, RuntimePackageBuilder.Valid().AsUnattended().AutoAcceptingWebContent().Build(), request).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Empty(server.Requests, "an unattended run must not send the request at all");
        AssertEx.Empty(cards);
        AssertEx.Contains(await HandlerResultAsync(request), ToolApprovalCoordinator.WebContentUnattendedMessage);
    }

    [Test]
    [Arguments("""{"url":5}""", "invalid-arguments")]
    [Arguments("""{"url":"http://10.0.0.1/admin"}""", "url-blocked")]
    public async Task RequestWebReviewAsync_WhenThereIsNothingToReview_TheRefusalReachesTheModelWithoutACard(string argumentsJson, string expectedError)
    {
        using var server = new WebReviewTestServer();
        using var scope = WebReviewResultScope.BeginScope();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());
        var request = new ToolApprovalRequestContent("approval-1",
            new FunctionCallContent("call-1", WebFetchToolDefinition.ToolName, JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)));

        await ReviewAsync(coordinator, RuntimePackageBuilder.Valid().Build(), request).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Empty(cards);
        AssertEx.Empty(server.Requests);
        AssertEx.Contains(await HandlerResultAsync(request), expectedError);
    }

    [Test]
    public async Task RetrieveWebContentAsync_RunsTheSegmentsWebCallsConcurrently()
    {
        using var server = new WebReviewTestServer
        {
            ConcurrentRequestsBeforeRelease = 2
        };
        var coordinator = CreateCoordinator(webReviewRetriever: server.CreateRetriever());
        ToolApprovalRequestContent[] requests =
        [
            WebRequest(WebFetchToolDefinition.ToolName, new
            {
                url = WebReviewTestServer.PageUrl
            }, "call-1"),
            new ToolApprovalRequestContent("approval-ask", new FunctionCallContent("call-ask", AskUserTool.ToolName)),
            WebRequest(WebSearchToolDefinition.ToolName, new
            {
                query = "tidal"
            }, "call-2")
        ];

        // Each stubbed request waits until BOTH are in flight, so a sequential retrieval would never finish.
        var retrievals = await coordinator.RetrieveWebContentAsync(RuntimePackageBuilder.Valid().Build(), requests, CancellationToken.None)
                                          .WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(expected: 2, retrievals.Count, "ask_user is not a web call and gets no retrieval");
        AssertEx.NotNull(retrievals[requests[0]].Preview);
        AssertEx.NotNull(retrievals[requests[2]].Preview);
    }

    [Test]
    public async Task RequestWebConsentAsync_WhenDenied_SendsNothing_AndTheModelGetsTheDeclineText()
    {
        using var server = new WebReviewTestServer();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(auditRecorder: auditRecorder, dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());
        var request = WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = "  HTTPS://News.Example.com/tidal  "
        });

        var pending = coordinator.RequestWebConsentAsync(RuntimePackageBuilder.Valid().Build(), request, static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => !cards.IsEmpty, TimeSpan.FromSeconds(5));
        var card = cards.Single();
        var preview = AssertEx.NotNull(card.WebReview);
        AssertEx.Equal(WebReviewPreview.RequestStage, preview.Stage);
        AssertEx.Equal(WebReviewTestServer.PageUrl, preview.Url, "the card shows the URL in the form the fetch sends it");
        AssertEx.Null(preview.Query);
        AssertEx.Null(preview.Text);
        AssertEx.Equal(expected: false, card.SessionScopeEligible, "consent is per request, never for the session");
        AssertEx.False(pending.IsCompleted, "the request waits on the user");

        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = card.RequestId,
            Approved = false
        }, ApprovalScope.Session);
        var refusal = AssertEx.NotNull(await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertEx.Null(refusal.Preview, "a declined request has no result to review");
        AssertEx.Contains(refusal.ModelText, ToolApprovalCoordinator.WebRequestDeclinedMessage);
        AssertEx.Empty(server.Requests, "a declined request never leaves the node");
        await AssertAuditedAsync(auditRecorder, WebFetchToolDefinition.ToolName, "web-request deny");
    }

    [Test]
    public async Task RequestWebConsentAsync_WhenAllowed_ShowsTheTrimmedQuery_AndLetsTheRequestGo()
    {
        using var server = new WebReviewTestServer();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(auditRecorder: auditRecorder, dispatcher: dispatcher, webReviewRetriever: server.CreateRetriever());
        var request = WebRequest(WebSearchToolDefinition.ToolName, new
        {
            query = "  tidal power ",
            maxResults = 3
        });

        var pending = coordinator.RequestWebConsentAsync(RuntimePackageBuilder.Valid().Build(), request, static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => !cards.IsEmpty, TimeSpan.FromSeconds(5));
        var preview = AssertEx.NotNull(cards.Single().WebReview);
        AssertEx.Equal(WebReviewPreview.RequestStage, preview.Stage);
        AssertEx.Equal("tidal power", preview.Query);
        AssertEx.Null(preview.Url);
        AssertEx.Empty(server.Requests, "nothing is sent while the card is open");

        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = cards.Single().RequestId,
            Approved = true
        });

        AssertEx.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)), "consent returns no refusal");
        await AssertAuditedAsync(auditRecorder, WebSearchToolDefinition.ToolName, "web-request approve");
    }

    [Test]
    public async Task RequestWebConsentAsync_WhenNobodyAnswersWithinTheAge_DeclinesInsteadOfExpiring()
    {
        var timeProvider = new ManualTimeProvider();
        var auditRecorder = Substitute.For<IToolApprovalAuditRecorder>();
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(auditRecorder: auditRecorder, dispatcher: dispatcher, timeProvider: timeProvider);
        var request = WebRequest(WebFetchToolDefinition.ToolName, new
        {
            url = WebReviewTestServer.PageUrl
        });

        var pending = coordinator.RequestWebConsentAsync(RuntimePackageBuilder.Valid().Build(), request, static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => !cards.IsEmpty && timeProvider.ArmedTimerCount > 0, TimeSpan.FromSeconds(5));
        timeProvider.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var refusal = AssertEx.NotNull(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertEx.Contains(refusal.ModelText, ToolApprovalCoordinator.WebRequestTimeoutMessage);
        await AssertAuditedAsync(auditRecorder, WebFetchToolDefinition.ToolName, "web-request timeout");
    }

    [Test]
    [Arguments("unattended")]
    [Arguments("auto")]
    [Arguments("blank-call-id")]
    [Arguments("invalid-arguments")]
    [Arguments("empty-query")]
    [Arguments("blocked-url")]
    public async Task RequestWebConsentAsync_WhenThereIsNoOneToAskOrNothingToSend_ShowsNoCard(string situation)
    {
        var (dispatcher, cards) = LifecycleRecordingDispatcher();
        var coordinator = CreateCoordinator(dispatcher: dispatcher);
        var package = situation switch
        {
            "unattended" => RuntimePackageBuilder.Valid().AsUnattended().Build(),
            "auto" => RuntimePackageBuilder.Valid().AutoAcceptingWebContent().Build(),
            _ => RuntimePackageBuilder.Valid().Build()
        };
        var request = situation switch
        {
            "blank-call-id" => WebRequest(WebFetchToolDefinition.ToolName, new
            {
                url = WebReviewTestServer.PageUrl
            }, callId: ""),
            "invalid-arguments" => new ToolApprovalRequestContent("approval-1",
                new FunctionCallContent("call-1", WebFetchToolDefinition.ToolName, JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"url":5}"""))),
            "empty-query" => WebRequest(WebSearchToolDefinition.ToolName, new
            {
                query = "   "
            }),
            "blocked-url" => WebRequest(WebFetchToolDefinition.ToolName, new
            {
                url = "http://10.0.0.1/admin"
            }),
            _ => WebRequest(WebFetchToolDefinition.ToolName, new
            {
                url = WebReviewTestServer.PageUrl
            })
        };

        var refusal = await coordinator.RequestWebConsentAsync(package, request, static _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Null(refusal, "the retrieval step decides these calls: it refuses them or auto mode fetches");
        AssertEx.Empty(cards);
    }

    private static async Task<string> ReviewAsync(ToolApprovalCoordinator coordinator, RuntimePackage package, ToolApprovalRequestContent request)
    {
        var retrievals = await coordinator.RetrieveWebContentAsync(package, [request], CancellationToken.None);
        return await coordinator.RequestWebReviewAsync(package, request, retrievals[request], static _ => { }, CancellationToken.None);
    }

    private static Task<string> HandlerResultAsync(ToolApprovalRequestContent request) =>
        new WebFetchToolHandler
        {
            ResolveCallId = () => request.ToolCall.CallId
        }.ExecuteAsync("{}");

    private static ToolApprovalRequestContent WebRequest(string toolName, object arguments, string callId = "call-1") =>
        new($"approval-{callId}", WebReviewToolHandlerTests.Call(toolName, arguments, callId));

    private static (IWorkerEventDispatcher Dispatcher, ConcurrentQueue<ApprovalLifecyclePayload> Cards) LifecycleRecordingDispatcher()
    {
        var cards = new ConcurrentQueue<ApprovalLifecyclePayload>();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportApprovalLifecycleAsync(Arg.Do<ApprovalLifecyclePayload>(cards.Enqueue)).Returns(Task.CompletedTask);
        return (dispatcher, cards);
    }

    private static async Task AssertAuditedAsync(IToolApprovalAuditRecorder auditRecorder, string toolName, string decision)
    {
        await auditRecorder.Received(1)
                           .RecordAsync(Arg.Any<Guid?>(),
                               toolName,
                               Arg.Any<ToolCategory>(),
                               decision,
                               Arg.Any<string>(),
                               Arg.Any<long>(),
                               Arg.Any<CancellationToken>());
    }

    private static async Task GrantSessionApprovalAsync(ToolApprovalCoordinator coordinator,
        RecordingApprovalDispatcher dispatcher,
        RuntimePackageBuilder packageBuilder)
    {
        var pending = coordinator.RequestToolApprovalAsync(packageBuilder.Build(), SkillApprovalRequest(), static _ => { }, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => dispatcher.Approvals.Count > 0 && !pending.IsCompleted, TimeSpan.FromSeconds(5));
        coordinator.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = dispatcher.Approvals[^1].RequestId,
            Approved = true
        }, ApprovalScope.Session);
        AssertEx.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    ///     Records the approval cards the coordinator reports, which is how a test learns the opaque request id it
    ///     must echo back. A substitute would need an argument matcher per call site for the same thing.
    /// </summary>
    private sealed class RecordingApprovalDispatcher : IWorkerEventDispatcher
    {
        private readonly IWorkerEventDispatcher _inner = Substitute.For<IWorkerEventDispatcher>();

        public List<ApprovalRequestPayload> Approvals { get; } = [];

        public InvocationState? CurrentInvocation => _inner.CurrentInvocation;

        public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged
        {
            add => _inner.InvocationStateChanged += value;
            remove => _inner.InvocationStateChanged -= value;
        }

        public event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged
        {
            add => _inner.ToolCallLifecycleChanged += value;
            remove => _inner.ToolCallLifecycleChanged -= value;
        }

        public event EventHandler<TurnNoticeChangedEventArgs>? TurnNoticeChanged
        {
            add => _inner.TurnNoticeChanged += value;
            remove => _inner.TurnNoticeChanged -= value;
        }

        public event EventHandler<ApprovalRequestedChangedEventArgs>? ApprovalRequestedChanged
        {
            add => _inner.ApprovalRequestedChanged += value;
            remove => _inner.ApprovalRequestedChanged -= value;
        }

        public event EventHandler<UserQuestionRequestedChangedEventArgs>? UserQuestionRequestedChanged
        {
            add => _inner.UserQuestionRequestedChanged += value;
            remove => _inner.UserQuestionRequestedChanged -= value;
        }

        public Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload)
        {
            Approvals.Add(payload);
            return Task.CompletedTask;
        }

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once) =>
            _inner.DispatchApprovalResolvedAsync(evt, scope);

        public Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package, CancellationToken cancellationToken = default) =>
            _inner.ReportInvocationAssignedAsync(package, cancellationToken);

        public Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk) =>
            _inner.ReportInvocationStreamChunkAsync(invocationId, chunk);

        public Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk) =>
            _inner.ReportInvocationThinkingChunkAsync(invocationId, chunk);

        public Task ReportInvocationTextReclassifiedAsync(Guid invocationId, string content, string reasoningSuffix) =>
            _inner.ReportInvocationTextReclassifiedAsync(invocationId, content, reasoningSuffix);

        public Task ReportInvocationPhaseAsync(Guid invocationId, InvocationRuntimePhase phase) =>
            _inner.ReportInvocationPhaseAsync(invocationId, phase);

        public Task ReportInvocationCompletedAsync(Guid invocationId,
            int? inputTokens = null,
            int? outputTokens = null,
            int? totalTokens = null,
            int? reasoningTokens = null,
            long? generationDurationMs = null,
            string? finishReason = null,
            InvocationThroughput? throughput = null) =>
            _inner.ReportInvocationCompletedAsync(invocationId, inputTokens, outputTokens, totalTokens, reasoningTokens, generationDurationMs, finishReason, throughput);

        public Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory) =>
            _inner.ReportInvocationFailedAsync(invocationId, failureMessage, failureCategory);

        public Task ReportToolSchemaTokensAsync(Guid invocationId, long? toolSchemaTokens, int? maxToolSchemaTokens) =>
            _inner.ReportToolSchemaTokensAsync(invocationId, toolSchemaTokens, maxToolSchemaTokens);

        public Task ReportTurnTelemetryAsync(Guid invocationId, long? modelReadinessMs, TurnUsageTotals? usage) =>
            _inner.ReportTurnTelemetryAsync(invocationId, modelReadinessMs, usage);

        public Task ReportTurnContextWindowAsync(Guid invocationId, int contextCapacityTokens, int reservedOutputTokens) =>
            _inner.ReportTurnContextWindowAsync(invocationId, contextCapacityTokens, reservedOutputTokens);

        public Task ReportEffortDispatchAsync(Guid invocationId, string dispatchedTier, string authoredEffort) =>
            _inner.ReportEffortDispatchAsync(invocationId, dispatchedTier, authoredEffort);

        public Task ReportServedModelAsync(Guid invocationId, string modelUsed) =>
            _inner.ReportServedModelAsync(invocationId, modelUsed);

        public Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload) =>
            _inner.ReportToolCallRequestedAsync(payload);

        public Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload) =>
            _inner.ReportToolCallLifecycleAsync(payload);

        public Task ReportTurnNoticeAsync(TurnNoticePayload payload) =>
            _inner.ReportTurnNoticeAsync(payload);

        public Task ReportApprovalLifecycleAsync(ApprovalLifecyclePayload payload) =>
            _inner.ReportApprovalLifecycleAsync(payload);

        public Task ReportUserQuestionAsync(UserQuestionLifecyclePayload payload) =>
            _inner.ReportUserQuestionAsync(payload);

        public Task DispatchUserQuestionAnsweredAsync(UserQuestionAnsweredEvent evt) =>
            _inner.DispatchUserQuestionAnsweredAsync(evt);
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

    private static ToolApprovalCoordinator CreateCoordinator(PendingToolCallRegistry? registry = null,
        IToolApprovalAuditRecorder? auditRecorder = null,
        IWorkerEventDispatcher? dispatcher = null,
        TimeProvider? timeProvider = null,
        WebReviewRetriever? webReviewRetriever = null,
        INodeRuntimeSettings? runtimeSettings = null)
    {
        return new ToolApprovalCoordinator(new Lazy<IWorkerEventDispatcher>(() => dispatcher ?? Substitute.For<IWorkerEventDispatcher>()),
            registry ?? new PendingToolCallRegistry(),
            auditRecorder ?? Substitute.For<IToolApprovalAuditRecorder>(),
            NodeToolApprovalPolicy.FromSettings(settings: null),
            new UserQuestionAnswerStash(TimeProvider.System),
            webReviewRetriever ?? WebReviewTestServer.IdleRetriever,
            runtimeSettings ?? StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).WithWebAccessEnabled(true).Build(),
            NullLogger<ToolApprovalCoordinator>.Instance,
            // The clock the pending call's CreatedAt is stamped from; a test that ages a call hands the SAME provider
            // to the ApiToolCallBridge whose sweep reads the cutoff off it.
            timeProvider ?? TimeProvider.System);
    }
}
