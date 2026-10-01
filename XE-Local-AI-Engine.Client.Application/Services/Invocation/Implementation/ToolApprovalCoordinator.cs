namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;

/// <summary>
///     Owns every human round-trip an invocation can park on: tool approvals, the session-scoped approval memo, the
///     <c>ask_user</c> question flow and the web request consent and result review.
/// </summary>
/// <remarks>
///     Separate from <see cref="InvocationRunner" /> so the security-critical ordering rules are reviewable in one
///     file: the unattended guard runs unconditionally BEFORE the session memo is consulted, and the
///     <see cref="MaxSessionApprovals" /> cap fails closed. A singleton, because the memo spans a conversation and a
///     pending approval or question is released by a post arriving on a different call stack than the waiting turn.
/// </remarks>
public sealed class ToolApprovalCoordinator
{
    // Coordinator-local audit labels extending the canonical ApprovalDecisions operator vocabulary with outcomes reached WITHOUT an
    // operator round-trip. A memo-suppressed approval is still audited, precisely so session scope cannot thin the trail invisibly.
    private const string SessionScopeApprovalDecision = "session-scope auto-approve";

    private const string UnattendedApprovalDecision = "unattended-unavailable";

    private const string WebContentAutoAcceptDecision = "web-content auto-accept";

    // The pre-request consent's outcomes, one row each, so the trail tells a declined request from a declined result.
    private const string WebRequestApproveDecision = "web-request approve";

    private const string WebRequestDenyDecision = "web-request deny";

    private const string WebRequestTimeoutDecision = "web-request timeout";

    internal const string WebRequestDeclinedMessage = "The user declined to send this web request, so nothing was sent.";

    internal const string WebRequestTimeoutMessage = "The user did not approve this web request in time, so nothing was sent.";

    internal const string WebContentDeclinedMessage = "The user declined to add this web content to the conversation.";

    internal const string WebContentUnattendedMessage = "Web content is unavailable in unattended runs (no one to review it).";

    internal const string WebContentTimeoutMessage = "The user did not review this web content in time, so it was not added to the conversation.";

    // Upper bound on remembered session approvals, so a long-lived node cannot grow the memo without limit; each entry is a conversation +
    // tool + skill + version + resource tuple. Overflow FAILS CLOSED and the operator is prompted again, so the cap only ever adds prompts.
    private const int MaxSessionApprovals = 256;

    // MAF's own parameter names on load_skill / read_skill_resource, pinned by hand because the package exposes the TOOL names as constants but
    // not the argument names. A rename in a package bump degrades fail-closed: the memo stops matching and every skill call prompts again.
    private const string SkillNameArgument = "skillName";

    private const string ResourceNameArgument = "resourceName";

    // The audited risk category of the three MAF skill tools, which reach the model through AIContextProviders and never through the package's tool OFFER,
    // so ResolveApprovalToolCategory cannot see them and they would audit as Unknown. Cataloguing them instead would move every skill-bearing agent's config hash.
    private static readonly Dictionary<string, ToolCategory> SkillToolCategories = new(StringComparer.Ordinal)
    {
        [AgentSkillsProvider.LoadSkillToolName] = ToolCategory.ReadLocal,
        [AgentSkillsProvider.ReadSkillResourceToolName] = ToolCategory.ReadLocal,
        [AgentSkillsProvider.RunSkillScriptToolName] = ToolCategory.WriteExecute
    };

    // Questions parked on the operator, keyed by the opaque request id the browser echoes back. Separate from _pendingToolCalls: an approval resolves
    // to a bool and a question to the operator's answers, and conflating them would let an approve/deny post release a question with no answer at all.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<UserQuestionAnswer>>> _pendingQuestions = new(StringComparer.Ordinal);

    // The SAME dictionary instance InvocationLifecycleTracker and ApiToolCallBridge hold (PendingToolCallRegistry): an approval registered here is released by
    // ResolveApprovalResult, cancelled by the tracker's cancel/drain path and swept by the bridge's stale cleanup. A second copy would strand all three.
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls;

    // Session-scoped approvals the operator explicitly granted (ApprovalScope.Session), used as a SET — the byte value is ignored. It lives on this singleton
    // because the memo has to outlive the turn that created it and span the conversation; never persisted, so a node restart forgets everything in here.
    private readonly ConcurrentDictionary<ApprovalMemoKey, byte> _sessionApprovals = new();

    // The memo key a pending approval WOULD be remembered under, written just before the request is broadcast while the skill context is still in hand and
    // removed by the waiter. An entry exists ONLY for an eligible request, which is what makes the eligibility rules impossible to bypass from the resolve side.
    private readonly ConcurrentDictionary<string, ApprovalMemoKey> _sessionApprovalCandidates = new(StringComparer.Ordinal);

    // The operator's "skill tools always prompt" switch, read once at construction off the composed node approval policy, so an edit applies on the next
    // restart like the rest of it. Only that policy carries it; any other IToolApprovalPolicy — the permissive floor, a test double — leaves session scope available.
    private readonly bool _skillSessionScopeDisabled;

    private readonly IToolApprovalAuditRecorder _approvalAuditRecorder;

    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;

    private readonly ILogger<ToolApprovalCoordinator> _logger;

    private readonly TimeSpan _maxPendingToolCallAge;

    private readonly INodeRuntimeSettings _runtimeSettings;

    private readonly TimeProvider _timeProvider;

    private readonly UserQuestionAnswerStash _userQuestionAnswerStash;

    private readonly WebReviewRetriever _webReviewRetriever;

    /// <summary>The effective startup snapshot used by approval and question waits, also bounding work-session parks.</summary>
    internal TimeSpan PendingToolCallAge => _maxPendingToolCallAge;

    public ToolApprovalCoordinator(Lazy<IWorkerEventDispatcher> eventDispatcher,
        PendingToolCallRegistry pendingToolCallRegistry,
        IToolApprovalAuditRecorder approvalAuditRecorder,
        IToolApprovalPolicy approvalPolicy,
        UserQuestionAnswerStash userQuestionAnswerStash,
        WebReviewRetriever webReviewRetriever,
        INodeRuntimeSettings runtimeSettings,
        ILogger<ToolApprovalCoordinator> logger,
        TimeProvider timeProvider)
    {
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        ArgumentNullException.ThrowIfNull(pendingToolCallRegistry);
        _pendingToolCalls = pendingToolCallRegistry.Calls;
        _approvalAuditRecorder = approvalAuditRecorder ?? throw new ArgumentNullException(nameof(approvalAuditRecorder));
        _userQuestionAnswerStash = userQuestionAnswerStash ?? throw new ArgumentNullException(nameof(userQuestionAnswerStash));
        _webReviewRetriever = webReviewRetriever ?? throw new ArgumentNullException(nameof(webReviewRetriever));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // The human-wait cap is read once at singleton construction from INodeRuntimeSettings, exactly as the runner and
        // the API tool-call bridge read it, so an operator edit applies on the next process restart and all three agree.
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _maxPendingToolCallAge = TimeSpan.FromMinutes(runtimeSettings.GetMaxPendingToolCallAgeMinutes());

        // A concrete-type test rather than a widened IToolApprovalPolicy: that interface is the cross-project contract for one call's yes/no verdict, and the
        // node-only session-scope knob has no place on it. Read through the SHARED predicate the tool-catalog response uses, so coordinator and card cannot disagree.
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        _skillSessionScopeDisabled = SessionApprovalEligibility.IsSessionScopeDisabled(approvalPolicy);
    }

    /// <summary>
    ///     Carries a framework-surfaced approval request across the approval transport and waits for the decision.
    /// </summary>
    /// <remarks>
    ///     Reuses the pending-tool-call approval completion (resolved by <see cref="ResolveApprovalResult" />) and the
    ///     pending-tool-call age as the wait timeout; the result feeds the runner's threadless resume. Two guards run
    ///     before anything is registered or broadcast and their ORDER is security-critical: the unattended check is
    ///     unconditional and FIRST, so no pre-authorisation populating the memo can satisfy an approval in a run with no
    ///     human in it. It covers every approval-required tool, not only skills — docs/wiki/04-agent-mode.md §4.6.
    /// </remarks>
    public async Task<bool> RequestToolApprovalAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        Action<bool> setInvocationDeadline,
        CancellationToken cancellationToken,
        string? descriptionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(approvalRequest);
        ArgumentNullException.ThrowIfNull(setInvocationDeadline);

        // Approval-decision audit: the tool name drives both the category lookup and the audit row, and the request-to-decision stopwatch times it. Both are
        // needed in the guards and in the timeout catch as well, so they live outside the try; the row and metric the decision records are content-free.
        var approvalToolName = (approvalRequest.ToolCall as FunctionCallContent)?.Name;
        var approvalRequestedTimestamp = Stopwatch.GetTimestamp();

        if (package.IsUnattended)
        {
            var reason = $"{ApprovalUnavailableException.UnattendedReasonPrefix}{approvalToolName ?? approvalRequest.ToolCall.CallId}";
            _logger.LogWarning("Failing unattended invocation {InvocationId}: {Reason}", package.InvocationId, reason);
            await RecordApprovalDecisionAuditAsync(package,
                approvalToolName,
                UnattendedApprovalDecision,
                approvalRequestedTimestamp,
                cancellationToken);
            throw new ApprovalUnavailableException(reason);
        }

        var sessionApprovalKey = TryResolveSessionApprovalKey(package, approvalRequest, approvalToolName);
        if (sessionApprovalKey is { } memoKey && _sessionApprovals.ContainsKey(memoKey))
        {
            // The operator already approved this exact tool, on this skill at this content version, for this resource, in this conversation. The prompt is
            // suppressed but the audit row is NOT: an approval that leaves no trace is how a session scope quietly thins the record of what an agent may do.
            await RecordApprovalDecisionAuditAsync(package,
                approvalToolName,
                SessionScopeApprovalDecision,
                approvalRequestedTimestamp,
                cancellationToken);
            return true;
        }

        try
        {
            var approved = await ParkForDecisionAsync(package,
                approvalRequest,
                approvalToolName,
                descriptionOverride ?? $"A tool call ({approvalRequest.ToolCall.CallId}) requires approval before it runs.",
                sessionApprovalKey,
                webReview: null,
                setInvocationDeadline,
                cancellationToken);

            await RecordApprovalDecisionAuditAsync(package,
                approvalToolName,
                approved ? ApprovalDecisions.Approve : ApprovalDecisions.Deny,
                approvalRequestedTimestamp,
                cancellationToken);
            return approved;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked CTS fired on the pending-tool-call age WITHOUT the invocation being cancelled: a genuine approval TIMEOUT, since an operator cancel trips
            // cancellationToken and skips this filter, propagating as a cancel. Audit it, then fail the turn naming the tool, the same failure the stale sweep raises.
            await RecordApprovalDecisionAuditAsync(package,
                approvalToolName,
                ApprovalDecisions.Timeout,
                approvalRequestedTimestamp,
                cancellationToken);
            throw new ApprovalExpiredException(approvalToolName);
        }
    }

    /// <summary>
    ///     Asks the user before a web call sends anything; <see langword="null" /> means go ahead, otherwise the refusal the
    ///     model gets instead.
    /// </summary>
    /// <remarks>
    ///     No card when unattended, with no call id or in auto mode (the retrieval handles those) or when the arguments are
    ///     refused before sending. Per request only, never session scope; a timeout declines instead of failing the turn.
    /// </remarks>
    internal async Task<WebReviewRetrieval?> RequestWebConsentAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        Action<bool> setInvocationDeadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(approvalRequest);
        ArgumentNullException.ThrowIfNull(setInvocationDeadline);

        if (package.IsUnattended
            || package.AutoAcceptWebContent
            || string.IsNullOrEmpty(approvalRequest.ToolCall.CallId)
            || approvalRequest.ToolCall is not FunctionCallContent call
            || WebReviewRetriever.DescribeRequest(call) is not { } preview)
        {
            return null;
        }

        var requestedTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var consented = await ParkForDecisionAsync(package,
                approvalRequest,
                call.Name,
                $"Allow {call.Name} to send this request?",
                sessionApprovalKey: null,
                preview,
                setInvocationDeadline,
                cancellationToken);

            await RecordApprovalDecisionAuditAsync(package,
                call.Name,
                consented ? WebRequestApproveDecision : WebRequestDenyDecision,
                requestedTimestamp,
                cancellationToken);
            return consented ? null : WebReviewRetrieval.Refusal("user-declined-request", WebRequestDeclinedMessage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RecordApprovalDecisionAuditAsync(package, call.Name, WebRequestTimeoutDecision, requestedTimestamp, cancellationToken);
            return WebReviewRetrieval.Refusal("request-consent-timeout", WebRequestTimeoutMessage);
        }
    }

    /// <summary>Retrieves every web call among <paramref name="approvalRequests" /> concurrently, ahead of their reviews.</summary>
    /// <remarks>
    ///     An unattended run and a call with no id fetch nothing: neither can ever deliver the content. A request that is not
    ///     a web call has no entry, so the common segment pays one scan.
    /// </remarks>
    internal async Task<IReadOnlyDictionary<ToolApprovalRequestContent, WebReviewRetrieval>> RetrieveWebContentAsync(RuntimePackage package,
        IReadOnlyList<ToolApprovalRequestContent> approvalRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(approvalRequests);

        var retrievals = new Dictionary<ToolApprovalRequestContent, Task<WebReviewRetrieval>>();
        foreach (var approvalRequest in approvalRequests)
        {
            if (!IsWebReviewRequest(approvalRequest))
            {
                continue;
            }

            retrievals[approvalRequest] = package.IsUnattended || string.IsNullOrEmpty(approvalRequest.ToolCall.CallId)
                ? Task.FromResult(WebReviewRetrieval.Refusal("web-review-unavailable", WebContentUnattendedMessage))
                : _webReviewRetriever.RetrieveAsync((FunctionCallContent)approvalRequest.ToolCall, cancellationToken);
        }

        var retrieved = await Task.WhenAll(retrievals.Values);
        return retrievals.Keys.Zip(retrieved).ToDictionary(static pair => pair.First, static pair => pair.Second);
    }

    /// <summary>
    ///     Runs the web result review for one retrieved call and returns the note riding its (always approving) response.
    /// </summary>
    /// <remarks>
    ///     Stashes what the handler returns: the fenced result on accept or auto, the decline text on reject, the host's
    ///     refusal when there is nothing to review. It parks on the same approval events as any tool, never offers session
    ///     scope, and never fails the turn; the unattended guard runs FIRST. See ADR 0017.
    /// </remarks>
    internal async Task<string> RequestWebReviewAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        WebReviewRetrieval retrieval,
        Action<bool> setInvocationDeadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(approvalRequest);
        ArgumentNullException.ThrowIfNull(retrieval);
        ArgumentNullException.ThrowIfNull(setInvocationDeadline);

        var toolName = (approvalRequest.ToolCall as FunctionCallContent)?.Name;
        var callId = approvalRequest.ToolCall.CallId;
        var requestedTimestamp = Stopwatch.GetTimestamp();

        if (package.IsUnattended)
        {
            WebReviewResultScope.Stash(callId, WebReviewRetrieval.RefusalJson("web-review-unavailable", WebContentUnattendedMessage));
            await RecordApprovalDecisionAuditAsync(package, toolName, UnattendedApprovalDecision, requestedTimestamp, cancellationToken);
            return "Not retrieved: this run has no one to review web content.";
        }

        if (retrieval.Preview is not { } preview)
        {
            WebReviewResultScope.Stash(callId, retrieval.ModelText);
            return "Nothing to review: the call was refused.";
        }

        // Auto also covers a review the operator's approval policy would add: on a single-admin node the operator is this user.
        if (package.AutoAcceptWebContent)
        {
            WebReviewResultScope.Stash(callId, await AcceptedWebContentAsync(retrieval, cancellationToken));
            await RecordApprovalDecisionAuditAsync(package, toolName, WebContentAutoAcceptDecision, requestedTimestamp, cancellationToken);
            return "Accepted automatically.";
        }

        try
        {
            var accepted = await ParkForDecisionAsync(package,
                approvalRequest,
                toolName,
                $"Review the content {toolName} retrieved before it is added to the conversation.",
                sessionApprovalKey: null,
                preview,
                setInvocationDeadline,
                cancellationToken);

            WebReviewResultScope.Stash(callId, accepted
                ? await AcceptedWebContentAsync(retrieval, cancellationToken)
                : WebReviewRetrieval.RefusalJson("user-declined", WebContentDeclinedMessage));
            await RecordApprovalDecisionAuditAsync(package,
                toolName,
                accepted ? ApprovalDecisions.Approve : ApprovalDecisions.Deny,
                requestedTimestamp,
                cancellationToken);
            return accepted ? "Accepted by user." : "Rejected by user.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No review in time: like ask_user, the turn continues without the content rather than failing.
            WebReviewResultScope.Stash(callId, WebReviewRetrieval.RefusalJson("review-timeout", WebContentTimeoutMessage));
            await RecordApprovalDecisionAuditAsync(package, toolName, ApprovalDecisions.Timeout, requestedTimestamp, cancellationToken);
            return "No review arrived in time.";
        }
    }

    // The switch is re-read at the moment content would enter the conversation: turning web access off while a card is
    // open refuses what it shows, as the services refuse every call made after it.
    private async Task<string> AcceptedWebContentAsync(WebReviewRetrieval retrieval, CancellationToken cancellationToken) =>
        await _runtimeSettings.GetWebAccessEnabledAsync(cancellationToken)
            ? retrieval.ModelText
            : WebReviewRetrieval.RefusalJson("web-access-disabled", "Web access is disabled on this node.");

    /// <summary>
    ///     Runs the <c>ask_user</c> human round-trip and returns the short, content-free note riding the approval response.
    /// </summary>
    /// <remarks>
    ///     Validates the model's questions, surfaces them to the operator, waits for the answers, and stashes the
    ///     resulting tool-result JSON under the tool call's id so <c>AskUserToolHandler</c> can return it the moment the
    ///     framework executes the (always-approved) call. NOTHING here fails the turn: a timeout, a cancelled browser, an
    ///     unattended run or arguments the model got wrong all stash an explicit "not answered" result and still approve.
    ///     Only a cancellation of the invocation itself propagates, because that turn is already ending.
    /// </remarks>
    public async Task<string> RequestUserAnswerAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        Action<bool> setInvocationDeadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(approvalRequest);
        ArgumentNullException.ThrowIfNull(setInvocationDeadline);

        // The SAME id-derivation the streaming tool-call lifecycle uses, so the browser attaches the question card to the tool-call card the
        // model is waiting on, and the handler's CurrentContext.CallContent.CallId lookup finds what is stashed here.
        var callId = InvocationRunner.ResolveToolCallCardId(approvalRequest.ToolCall.CallId, AskUserTool.ToolName);

        // ResolveToolCallCardId resolves a blank CallId to the tool name, so this key is never blank. With no provider id the handler still looks up its own
        // blank id, misses and returns its fail-safe — the right degradation, since the framework has nothing to correlate on and a wrong answer is worse than none.
        var stashKey = callId;

        if (!UserQuestionParser.TryParse((approvalRequest.ToolCall as FunctionCallContent)?.Arguments, out var questions, out var parseError))
        {
            // Never prompt an operator with unvalidated model output: tell the MODEL its call was malformed and let it retry, while the operator sees nothing.
            // The parse error is a fixed-shape structural sentence, so no operator content and no raw model text reaches the log.
            _logger.LogInformation("Rejected a malformed {ToolName} call for invocation {InvocationId} without prompting the operator: {Reason}",
                AskUserTool.ToolName,
                package.InvocationId,
                parseError);
            _userQuestionAnswerStash.Stash(stashKey, UserQuestionResults.Unanswered(UserQuestionResults.MalformedCallReason, parseError));
            return "The question was not shown: the call's arguments were invalid.";
        }

        // An UNATTENDED run has nobody to show the question to, so skip the park and hand the model the same "not answered" result the wait would reach anyway,
        // without the MaxPendingToolCallAge idle. The asymmetry with the approval path is deliberate and must survive tidying — docs/wiki/04-agent-mode.md §4.6.
        if (package.IsUnattended)
        {
            _logger.LogInformation("Skipped the {ToolName} prompt for unattended invocation {InvocationId}; the turn continues without an answer.",
                AskUserTool.ToolName,
                package.InvocationId);
            _userQuestionAnswerStash.Stash(stashKey, UserQuestionResults.Unanswered(UserQuestionResults.UnattendedReason));
            return "The question was not shown: this run has no operator to answer it.";
        }

        var requestId = Guid.NewGuid().ToString("N");
        var questionCompletion = new TaskCompletionSource<IReadOnlyList<UserQuestionAnswer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingQuestions.TryAdd(requestId, questionCompletion))
        {
            throw new InvalidOperationException("Failed to register pending user question.");
        }

        try
        {
            await _eventDispatcher.Value.ReportUserQuestionAsync(new UserQuestionLifecyclePayload
            {
                InvocationId = package.InvocationId,
                RequestId = requestId,
                CallId = callId,
                ToolName = AskUserTool.ToolName,
                Questions = questions
            });

            // The hard cap on any human wait, linked to the invocation token so a user cancel or shutdown still ends the wait promptly.
            // SetInvocationDeadline below is what stops the invocation's own, shorter budget from pre-empting this cap.
            using var questionTimeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            questionTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

            IReadOnlyList<UserQuestionAnswer> answers;
            setInvocationDeadline(true);
            try
            {
                answers = await questionCompletion.Task.WaitAsync(questionTimeoutCancellationTokenSource.Token);
            }
            finally
            {
                setInvocationDeadline(false);
            }

            _userQuestionAnswerStash.Stash(stashKey, UserQuestionResults.Answered(answers));
            return "The user answered.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The pending-question cap elapsed WITHOUT the invocation being cancelled: a genuine no-answer. Unlike the approval path, which rethrows and
            // fails the turn, this one must continue, so it swallows the timeout and hands the model an explicit "not answered" result.
            _logger.LogInformation("No answer arrived for the pending {ToolName} question on invocation {InvocationId}; the turn continues without one.",
                AskUserTool.ToolName,
                package.InvocationId);
            _userQuestionAnswerStash.Stash(stashKey, UserQuestionResults.Unanswered(UserQuestionResults.TimeoutReason));
            return "No answer arrived in time.";
        }
        finally
        {
            _pendingQuestions.TryRemove(requestId, out _);
        }
    }

    public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!_pendingToolCalls.TryGetValue(evt.RequestId, out var pendingToolCall))
        {
            return;
        }

        // Remembered for the rest of the conversation only when all of it lines up: the operator asked for session scope, the decision is an APPROVE (a deny is
        // never remembered, see ApprovalScope), and the request was registered memo-eligible when raised. Those rules live on the registration side alone.
        if (scope == ApprovalScope.Session && evt.Approved && _sessionApprovalCandidates.TryGetValue(evt.RequestId, out var memoKey))
        {
            RememberSessionApproval(memoKey);
        }

        pendingToolCall.ApprovalCompletion.TrySetResult(evt.Approved);
    }

    public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // TryGetValue (not TryRemove) mirrors ResolveApprovalResult: the waiter owns removal in its finally, and
        // TrySetResult makes the FIRST answer win, so a duplicate or stale post is a no-op rather than a fault.
        if (_pendingQuestions.TryGetValue(evt.RequestId, out var questionCompletion))
        {
            questionCompletion.TrySetResult(evt.Answers);
        }
    }

    // Registers the pending call, broadcasts both approval events and waits on the operator. Throws OperationCanceledException on the
    // pending-age timeout as on a cancel; callers tell them apart. The registration is removed however the wait ends.
    private async Task<bool> ParkForDecisionAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        string? toolName,
        string description,
        ApprovalMemoKey? sessionApprovalKey,
        WebReviewPreview? webReview,
        Action<bool> setInvocationDeadline,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var approvalCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingToolCall = new PendingToolCall
        {
            InvocationId = package.InvocationId,
            CreatedAt = _timeProvider.GetUtcNow(),
            ApprovalCompletion = approvalCompletion,
            ToolName = toolName
        };
        var dispatcher = _eventDispatcher.Value;

        if (!_pendingToolCalls.TryAdd(requestId, pendingToolCall))
        {
            throw new InvalidOperationException("Failed to register pending tool approval.");
        }

        // Only a memo-ELIGIBLE request gets a candidate key, so an "approve for this session" decision on anything else — run_skill_script, a non-skill tool,
        // an imported skill, a web review, or any tool at all while the operator's always-prompt switch is on — resolves as a one-shot approval.
        if (sessionApprovalKey is { } candidateKey)
        {
            _sessionApprovalCandidates[requestId] = candidateKey;
        }

        try
        {
            await dispatcher.ReportApprovalRequestedAsync(new ApprovalRequestPayload
            {
                InvocationId = package.InvocationId,
                RequestId = requestId,
                Description = description
            });

            // Surface the pending approval on the LOCAL chat stream, deriving the CallId through the SAME helper the streaming tool-call lifecycle uses, so both
            // events resolve one id and the browser attaches Approve/Deny to the matching card. ToolCall is the base ToolCallContent; FunctionCallContent carries the name.
            var approvalCallId = InvocationRunner.ResolveToolCallCardId(approvalRequest.ToolCall.CallId, toolName);
            await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
            {
                InvocationId = package.InvocationId,
                RequestId = requestId,
                CallId = approvalCallId,
                ToolName = string.IsNullOrEmpty(toolName) ? approvalCallId : toolName,
                Description = description,
                // The operator approves WHAT runs, and the tool-call-requested card carrying the arguments only arrives after the decision, so the
                // prompt carries them itself, serialized exactly as that lifecycle event serializes them.
                Arguments = approvalRequest.ToolCall is FunctionCallContent { Arguments: { } approvalArguments }
                    ? JsonSerializer.Serialize(approvalArguments)
                    : null,
                // The coordinator already resolved whether this exact call can be memoized, so it is the authority on whether the card may offer "Approve for
                // this session". The node tool catalog carries no MAF skill tool, so falling back to it would offer the button where the click degrades to "Once".
                SessionScopeEligible = sessionApprovalKey is not null,
                WebReview = webReview
            });

            // The age runs on the injected clock, so the expiry is testable; linking keeps an invocation cancel distinguishable in the callers' catch.
            using var approvalAgeCancellationTokenSource = new CancellationTokenSource(_maxPendingToolCallAge, _timeProvider);
            using var approvalTimeoutCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, approvalAgeCancellationTokenSource.Token);

            // One line per park, so a turn that looks frozen in the logs shows it is waiting on the operator. Ids and the tool name only, never arguments.
            _logger.LogInformation("Invocation {InvocationId} is waiting on operator decision {RequestId} ({DecisionKind}) for tool {ToolName}",
                package.InvocationId,
                requestId,
                webReview is null ? "approval" : "web-" + webReview.Stage,
                toolName);

            setInvocationDeadline(true);
            try
            {
                return await approvalCompletion.Task.WaitAsync(approvalTimeoutCancellationTokenSource.Token);
            }
            finally
            {
                setInvocationDeadline(false);
            }
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
            _sessionApprovalCandidates.TryRemove(requestId, out _);
        }
    }

    /// <summary>Whether this approval request has already been captured for the current segment.</summary>
    /// <remarks>
    ///     Prefers a namespaced stable key — the tool-call id, else the approval's own request id — so a provider
    ///     re-emitting one request across streamed chunks enqueues it once. A BLANK call id must never bypass dedup, which
    ///     would prompt N times and dangle N-1 ambiguous responses for a single call; with neither id present it falls
    ///     back to reference identity, so at least the same surfaced instance is not enqueued twice.
    ///     <paramref name="seenKeys" /> accumulates this segment's keys, and a stable key is added here on first sight.
    /// </remarks>
    public static bool IsDuplicatePendingApproval(ToolApprovalRequestContent approvalRequest,
        List<ToolApprovalRequestContent> pendingApprovals,
        HashSet<string> seenKeys)
    {
        string? key = null;
        if (!string.IsNullOrEmpty(approvalRequest.ToolCall.CallId))
        {
            key = "call:" + approvalRequest.ToolCall.CallId;
        }
        else if (!string.IsNullOrEmpty(approvalRequest.RequestId))
        {
            key = "req:" + approvalRequest.RequestId;
        }

        if (key is not null)
        {
            return !seenKeys.Add(key);
        }

        // No stable identifier at all: dedup by reference identity so the same instance is not captured twice.
        return pendingApprovals.Contains(approvalRequest);
    }

    /// <summary>Whether a framework-surfaced approval request belongs to <c>ask_user</c>.</summary>
    /// <remarks>
    ///     Matched on the tool NAME rather than on any flag, because the name is the only thing that survives the
    ///     framework's approval wrapping: <c>ToolApprovalRequestContent.ToolCall</c> is the base type and the concrete
    ///     <see cref="FunctionCallContent" /> is what carries it.
    /// </remarks>
    public static bool IsUserQuestionRequest(ToolApprovalRequestContent approvalRequest) =>
        string.Equals((approvalRequest.ToolCall as FunctionCallContent)?.Name, AskUserTool.ToolName, StringComparison.Ordinal);

    /// <summary>Whether a framework-surfaced approval request is a web result review; matched on the tool name as for <c>ask_user</c>.</summary>
    public static bool IsWebReviewRequest(ToolApprovalRequestContent approvalRequest) =>
        approvalRequest.ToolCall is FunctionCallContent call && WebAccessToolCatalog.IsWebTool(call.Name);

    /// <summary>
    ///     The <see cref="ApprovalMemoKey" /> this request may be remembered under, or <see langword="null" /> when it is
    ///     not eligible for a session-scoped approval at all.
    /// </summary>
    /// <remarks>
    ///     Everything about the memo's reach is decided here: the node-level always-prompt switch turns eligibility off
    ///     entirely; the tool must be one of MAF's two READ-ONLY skill tools, and <c>run_skill_script</c> stays excluded;
    ///     the named skill must be in this package's resolved set, which supplies the VERSION the approval binds to; an
    ///     IMPORTED skill is never eligible; and <c>read_skill_resource</c> must name its resource. Those names are only
    ///     reachable by reading the model's own call arguments. Rationale: docs/wiki/04-agent-mode.md §4.6.
    /// </remarks>
    private ApprovalMemoKey? TryResolveSessionApprovalKey(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        string? toolName)
    {
        if (_skillSessionScopeDisabled || string.IsNullOrEmpty(toolName))
        {
            return null;
        }

        // Custom-tool branch, resolved BEFORE the skill-only guards. Session-approvable ONLY when Fixed, whose verbatim operator-authored invocation the model
        // cannot alter; a Parameterized tool returns null so every argument set re-prompts. Bound to the tool's Version via SkillVersion, so an edit re-prompts.
        if (SessionApprovalEligibility.IsCustomToolName(toolName))
        {
            if (package.CustomTools is not { Count: > 0 } customTools)
            {
                return null;
            }

            var customTool = customTools.FirstOrDefault(candidate => string.Equals(candidate.Name, toolName, StringComparison.Ordinal));
            if (customTool is null || !SessionApprovalEligibility.IsToolEligible(toolName, customTool.IsFixed))
            {
                return null;
            }

            return new ApprovalMemoKey(package.ConversationId, toolName, customTool.Name, customTool.Version, ResourceName: null);
        }

        if (package.Skills is not { Count: > 0 } skills)
        {
            return null;
        }

        if (!SessionApprovalEligibility.IsToolEligible(toolName, isFixedCustomTool: false))
        {
            return null;
        }

        var isResourceRead = string.Equals(toolName, AgentSkillsProvider.ReadSkillResourceToolName, StringComparison.Ordinal);

        var call = approvalRequest.ToolCall as FunctionCallContent;
        if (ReadStringArgument(call, SkillNameArgument) is not { } skillName)
        {
            return null;
        }

        var skill = skills.FirstOrDefault(candidate => string.Equals(candidate.Name, skillName, StringComparison.Ordinal));
        if (skill is null || skill.IsImported)
        {
            return null;
        }

        string? resourceName = null;
        if (isResourceRead && (resourceName = ReadStringArgument(call, ResourceNameArgument)) is null)
        {
            return null;
        }

        return new ApprovalMemoKey(package.ConversationId, toolName, skill.Name, skill.Version, resourceName);
    }

    // Adds a granted session approval, refusing new entries once the cap is reached. Refusing is the fail-closed
    // direction: the memo stops suppressing prompts and the operator is asked again.
    private void RememberSessionApproval(ApprovalMemoKey memoKey)
    {
        if (_sessionApprovals.Count >= MaxSessionApprovals && !_sessionApprovals.ContainsKey(memoKey))
        {
            _logger.LogWarning("Session-scoped approval memo is at its {Cap}-entry cap; the approval was applied to this call only.", MaxSessionApprovals);
            return;
        }

        _sessionApprovals[memoKey] = 0;
    }

    // Resolves the audited category from the offered tool's declared ToolCategory and hands it, with the decision, to the recorder.
    // The recorder swallows every failure, so this can never throw into — or stall — the approval round-trip.
    private async Task RecordApprovalDecisionAuditAsync(RuntimePackage package,
        string? toolName,
        string decision,
        long requestedTimestamp,
        CancellationToken cancellationToken)
    {
        var latencyMs = (long)Stopwatch.GetElapsedTime(requestedTimestamp).TotalMilliseconds;
        var category = ResolveApprovalToolCategory(package, toolName);
        await _approvalAuditRecorder.RecordAsync(package.InvocationId,
            toolName ?? string.Empty,
            category,
            decision,
            ApprovalDecisionSources.Local,
            latencyMs,
            cancellationToken);
    }

    // The offered tool's declared risk category, matched by name against the same categorized package offer the policy layer evaluates, falling back to the
    // policy's own fail-closed Unknown. The provider-injected skill tools are checked FIRST: never in the offer, they would otherwise audit as Unknown too.
    private static ToolCategory ResolveApprovalToolCategory(RuntimePackage package, string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return ToolCategory.Unknown;
        }

        if (SkillToolCategories.TryGetValue(toolName, out var skillToolCategory))
        {
            return skillToolCategory;
        }

        var offer = package.AllowedTools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        return offer?.Category ?? ToolCategory.Unknown;
    }

    // A non-empty string argument off a function call, tolerating both the deserialized-string and the raw JsonElement shapes providers hand the framework.
    // Anything else — absent, null, a number, an object — yields null, which the caller treats as "not eligible": the memo fails closed on what it cannot read.
    private static string? ReadStringArgument(FunctionCallContent? call, string argumentName)
    {
        if (call?.Arguments is not { } arguments || !arguments.TryGetValue(argumentName, out var value))
        {
            return null;
        }

        var text = value switch
        {
            string stringValue => stringValue,
            JsonElement { ValueKind: JsonValueKind.String } jsonValue => jsonValue.GetString(),
            _ => null
        };

        return string.IsNullOrEmpty(text) ? null : text;
    }
}
