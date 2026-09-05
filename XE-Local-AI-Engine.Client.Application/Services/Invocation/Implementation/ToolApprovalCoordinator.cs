namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Owns every human round-trip an invocation can park on: the tool-approval request/decision cycle, the
///     session-scoped approval memo, and the <c>ask_user</c> question flow. Extracted from
///     <see cref="InvocationRunner" /> so the security-critical ordering rules — the unattended guard running
///     unconditionally BEFORE the session memo is consulted, and the fail-closed
///     <see cref="MaxSessionApprovals" /> cap — are reviewable in one file.
///     <para>
///         A singleton, because the state it owns outlives the turn that created it: the session memo spans a
///         conversation, and a pending approval or question is released by an HTTP/hub post that arrives on a
///         different call stack than the turn waiting for it.
///     </para>
/// </summary>
public sealed class ToolApprovalCoordinator
{
    // These coordinator-local audit labels intentionally extend the canonical ApprovalDecisions operator vocabulary
    // with outcomes reached WITHOUT an operator round-trip. A memo-suppressed approval is still audited precisely so
    // session scope cannot thin the trail invisibly.
    private const string SessionScopeApprovalDecision = "session-scope auto-approve";

    private const string UnattendedApprovalDecision = "unattended-unavailable";

    // Upper bound on remembered session approvals. Each entry is a conversation + tool + skill + version + resource
    // tuple, so reaching this needs hundreds of distinct deliberate approvals; the cap exists so a long-lived node
    // cannot grow the memo without limit. Overflow FAILS CLOSED — the memo simply stops accepting new entries and the
    // operator is prompted again — so the cap can only ever add prompts, never remove one.
    private const int MaxSessionApprovals = 256;

    // MAF's own parameter names on load_skill / read_skill_resource. The package exposes the TOOL names as constants but
    // not the argument names, so these are pinned by hand. A rename in a future package bump degrades fail-closed: the
    // memo stops matching, every skill call prompts again, and nothing is auto-approved that should not be.
    private const string SkillNameArgument = "skillName";

    private const string ResourceNameArgument = "resourceName";

    // The audited risk category of the three MAF skill tools. They reach the model through AIContextProviders
    // (progressive disclosure), never through the package's tool OFFER, so the offer lookup in
    // ResolveApprovalToolCategory cannot see them and every skill approval was auditing as Unknown. Registering them in
    // the tool catalog instead would move the config hash for every skill-bearing agent (and needs an executable that
    // does not exist), so the audit is fixed here, where the only thing missing was a name.
    private static readonly Dictionary<string, ToolCategory> SkillToolCategories = new(StringComparer.Ordinal)
    {
#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI; the same scoped suppression the provider call sites use.
        [AgentSkillsProvider.LoadSkillToolName] = ToolCategory.ReadLocal,
        [AgentSkillsProvider.ReadSkillResourceToolName] = ToolCategory.ReadLocal,
        [AgentSkillsProvider.RunSkillScriptToolName] = ToolCategory.WriteExecute
#pragma warning restore MAAI001
    };

    // Questions parked on the operator, keyed by the opaque request id the browser echoes back. Deliberately separate
    // from _pendingToolCalls: an approval resolves to a bool, a question resolves to the operator's answers, and
    // conflating them would let an approve/deny post release a question with no answer at all.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<UserQuestionAnswer>>> _pendingQuestions = new(StringComparer.Ordinal);

    // The SAME dictionary instance the runner and ApiToolCallBridge hold (see PendingToolCallRegistry): an approval
    // registered here is released by ResolveApprovalResult, cancelled by the runner's cancel/drain path, and swept by
    // the bridge's stale cleanup. A second copy would strand every one of those.
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls;

    // Session-scoped approvals the operator explicitly granted (ApprovalScope.Session), used as a SET — the byte value
    // is ignored. Lives on this singleton coordinator, next to _pendingToolCalls/_pendingQuestions, because the memo has to
    // outlive the turn that created it: an approval agent is scoped to one invocation and could never span the
    // conversation. Never persisted, so a node restart forgets everything in here.
    private readonly ConcurrentDictionary<ApprovalMemoKey, byte> _sessionApprovals = new();

    // The memo key a currently-pending approval WOULD be remembered under, keyed by the approval request id. It is
    // written just before the request is broadcast, while the skill context is still in hand, and removed again by the
    // waiter. An entry exists ONLY for a memo-eligible request, which is what makes the eligibility rules — the two
    // read-only skill tools, a locally authored skill, session scope enabled — impossible to bypass from the resolve
    // side.
    private readonly ConcurrentDictionary<string, ApprovalMemoKey> _sessionApprovalCandidates = new(StringComparer.Ordinal);

    // The operator's "skill tools always prompt" switch, read once at singleton construction off the composed node
    // approval policy (an operator edit applies on the next node restart, like the rest of that policy). Only the node
    // policy carries it: any other IToolApprovalPolicy — the AI.Agent permissive floor, a test double — leaves session
    // scope available, which is the pre-existing behaviour for every deployment that has not set the knob.
    private readonly bool _skillSessionScopeDisabled;

    private readonly IToolApprovalAuditRecorder _approvalAuditRecorder;

    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;

    private readonly Lazy<IHubMessageSender> _hubSender;

    private readonly ILogger<ToolApprovalCoordinator> _logger;

    private readonly TimeSpan _maxPendingToolCallAge;

    private readonly UserQuestionAnswerStash _userQuestionAnswerStash;

    public ToolApprovalCoordinator(Lazy<IHubMessageSender> hubSender,
        Lazy<IWorkerEventDispatcher> eventDispatcher,
        PendingToolCallRegistry pendingToolCallRegistry,
        IToolApprovalAuditRecorder approvalAuditRecorder,
        IToolApprovalPolicy approvalPolicy,
        UserQuestionAnswerStash userQuestionAnswerStash,
        INodeRuntimeSettings runtimeSettings,
        ILogger<ToolApprovalCoordinator> logger)
    {
        _hubSender = hubSender ?? throw new ArgumentNullException(nameof(hubSender));
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        ArgumentNullException.ThrowIfNull(pendingToolCallRegistry);
        _pendingToolCalls = pendingToolCallRegistry.Calls;
        _approvalAuditRecorder = approvalAuditRecorder ?? throw new ArgumentNullException(nameof(approvalAuditRecorder));
        _userQuestionAnswerStash = userQuestionAnswerStash ?? throw new ArgumentNullException(nameof(userQuestionAnswerStash));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The human-wait cap is read once at singleton construction from INodeRuntimeSettings, exactly as the runner and
        // the API tool-call bridge read it, so an operator edit applies on the next process restart and all three agree.
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _maxPendingToolCallAge = TimeSpan.FromMinutes(runtimeSettings.GetMaxPendingToolCallAgeMinutes());

        // A concrete-type test rather than a widened IToolApprovalPolicy: the interface is the cross-project AI.Agent
        // contract for one call's yes/no verdict, and the node-only session-scope knob has no place on it. Any other
        // implementation leaves session scope available (today's behaviour). Read through the SHARED predicate the node
        // tool-catalog response also uses, so the coordinator and the chat card can never disagree about the switch.
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        _skillSessionScopeDisabled = SessionApprovalEligibility.IsSessionScopeDisabled(approvalPolicy);
    }

    /// <summary>
    ///     Carries a framework-surfaced <see cref="ToolApprovalRequestContent" /> across the existing approval
    ///     transport and waits for the remote/local decision. Reuses the <see cref="_pendingToolCalls" /> approval
    ///     completion (resolved by <see cref="ResolveApprovalResult" />) and the pending-tool-call age as the wait
    ///     timeout. The result feeds the threadless resume in
    ///     <see cref="InvocationRunner.RunAsync(InvocationExecutionContext, CancellationToken)" />.
    ///     <para>
    ///         Two guards run BEFORE anything is registered or broadcast, and their ORDER is security-critical. The
    ///         unattended check comes first and is unconditional: a run with no human on the other end can never obtain
    ///         an approval, so it fails immediately rather than parking on a card nobody will see. Only then is the
    ///         session memo consulted. Inverting the two would let any future pre-authorisation feature that populates
    ///         the memo become a way to satisfy approvals inside an unattended run — exactly the property the unattended
    ///         guard exists to deny. Note the blast radius of the first guard honestly: it applies to EVERY
    ///         approval-required tool an unattended run can reach, not only the skill tools, and that is intended.
    ///     </para>
    /// </summary>
    public async Task<bool> RequestToolApprovalAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        Action<bool> setInvocationDeadline,
        CancellationToken cancellationToken,
        string? descriptionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(approvalRequest);
        ArgumentNullException.ThrowIfNull(setInvocationDeadline);

        // Approval-decision audit: the tool name (drives both the category lookup and the audit row) and the
        // request→decision stopwatch are captured here so the resolved decision below can record a content-free audit row
        // and metric. Both are needed in the guards and in the timeout catch as well, so they live outside the try.
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
                cancellationToken).ConfigureAwait(false);
            throw new ApprovalUnavailableException(reason);
        }

        var sessionApprovalKey = TryResolveSessionApprovalKey(package, approvalRequest, approvalToolName);
        if (sessionApprovalKey is { } memoKey && _sessionApprovals.ContainsKey(memoKey))
        {
            // The operator already approved this exact skill tool, on this skill at this content version, for this
            // resource, in this conversation. The prompt is suppressed — but the audit row is NOT: an approval that
            // leaves no trace is how a session scope quietly thins the record of what an agent was allowed to do.
            await RecordApprovalDecisionAuditAsync(package,
                approvalToolName,
                SessionScopeApprovalDecision,
                approvalRequestedTimestamp,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var approvalCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultCompletion = new TaskCompletionSource<ToolCallResultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingToolCall = new PendingToolCall(package.InvocationId, DateTimeOffset.UtcNow, approvalCompletion, resultCompletion);
        var sender = _hubSender.Value;
        var dispatcher = _eventDispatcher.Value;

        if (!_pendingToolCalls.TryAdd(requestId, pendingToolCall))
        {
            throw new InvalidOperationException("Failed to register pending tool approval.");
        }

        // Only a memo-ELIGIBLE request gets a candidate key, so an "approve for this session" decision on anything else
        // (run_skill_script, a non-skill tool, an imported skill, or any tool at all while the operator's
        // always-prompt switch is on) resolves as a plain one-shot approval and is never remembered.
        if (sessionApprovalKey is { } candidateKey)
        {
            _sessionApprovalCandidates[requestId] = candidateKey;
        }

        try
        {
            var approvalPayload = new ApprovalRequestPayload
            {
                InvocationId = package.InvocationId,
                RequestId = requestId,
                Description = descriptionOverride
                              ?? $"A tool call ({approvalRequest.ToolCall.CallId}) requires approval before it runs."
            };

            // The hub send exists for the PAIRED case only, and is skipped for a loopback turn exactly as every other
            // hub send on the invocation path is (InvocationRunner.RunAsync's shouldSendHubMessages). A standalone node
            // has no worker hub, so sending unconditionally threw before the local dispatch below could run — failing
            // the whole turn instead of rendering the approval card the operator answers.
            if (!InvocationRunner.IsLocalLoopbackInvocation(package))
            {
                await sender.SendApprovalRequestAsync(approvalPayload, cancellationToken).ConfigureAwait(false);
            }

            await dispatcher.ReportApprovalRequestedAsync(approvalPayload).ConfigureAwait(false);

            // Surface the pending approval on the LOCAL chat stream. The CallId is derived through the SAME
            // helper the streaming tool-call-requested lifecycle uses (CallId, falling back to the tool name when it is
            // absent OR blank) so both events resolve the identical id, and the browser can
            // attach the Approve/Deny controls to the matching tool-call card. In desktop/local mode there is no worker
            // hub to resolve the approval, so the loopback resolve endpoint feeds ResolveApprovalResult below. ToolCall
            // is the base ToolCallContent (CallId only); the concrete FunctionCallContent carries the tool name.
            var approvalCallId = InvocationRunner.ResolveToolCallCardId(approvalRequest.ToolCall.CallId, approvalToolName);
            await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
            {
                InvocationId = package.InvocationId,
                RequestId = requestId,
                CallId = approvalCallId,
                ToolName = string.IsNullOrEmpty(approvalToolName) ? approvalCallId : approvalToolName,
                Description = approvalPayload.Description,
                // The coordinator already resolved whether this exact call can be memoized (sessionApprovalKey above),
                // so it is the authority on whether the card may offer "Approve for this session". Without it the card
                // fell back to the node tool catalog, which does not carry the MAF skill tools at all and therefore
                // offered the button for run_skill_script and imported skills, where the click silently degraded to
                // "Once".
                SessionScopeEligible = sessionApprovalKey is not null
            }).ConfigureAwait(false);

            using var approvalTimeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            approvalTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

            bool approved;
            setInvocationDeadline(true);
            try
            {
                approved = await approvalCompletion.Task.WaitAsync(approvalTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
            }
            finally
            {
                setInvocationDeadline(false);
            }

            await RecordApprovalDecisionAuditAsync(package,
                approvalToolName,
                approved ? ApprovalDecisions.Approve : ApprovalDecisions.Deny,
                approvalRequestedTimestamp,
                cancellationToken).ConfigureAwait(false);
            return approved;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked CTS fired on the pending-tool-call age WITHOUT the invocation being cancelled: a genuine approval
            // TIMEOUT (an operator/user cancel trips cancellationToken and skips this filter, propagating as a cancel).
            // Audit it, then rethrow so the turn still fails EXACTLY as before — the audit only observes, never alters flow.
            await RecordApprovalDecisionAuditAsync(package,
                approvalToolName,
                ApprovalDecisions.Timeout,
                approvalRequestedTimestamp,
                cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
            _sessionApprovalCandidates.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    ///     Runs the <c>ask_user</c> human round-trip: validates the model's questions, surfaces them to the operator,
    ///     waits for the answers, and stashes the resulting tool-result JSON under the tool call's <c>CallId</c> so
    ///     <c>AskUserToolHandler</c> can return it the moment the framework executes the (always-approved) call. Returns
    ///     the short, content-free note that rides the approval response.
    ///     <para>
    ///         NOTHING here fails the turn. A timeout, a cancelled browser, an unattended run, or
    ///         arguments the model got wrong all stash an explicit "not answered" result and still approve, so the model
    ///         receives a clean, branchable answer instead of a dead turn. Only a cancellation of the invocation itself
    ///         propagates — the turn is already ending.
    ///     </para>
    /// </summary>
    public async Task<string> RequestUserAnswerAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        Action<bool> setInvocationDeadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(approvalRequest);
        ArgumentNullException.ThrowIfNull(setInvocationDeadline);

        // The SAME id-derivation the streaming tool-call lifecycle uses, so the browser attaches the question card to
        // the tool-call card the model is waiting on — and so the handler's CurrentContext.CallContent.CallId lookup
        // finds what is stashed here.
        var callId = InvocationRunner.ResolveToolCallCardId(approvalRequest.ToolCall.CallId, AskUserTool.ToolName);

        // ResolveToolCallCardId already resolves a blank CallId to the tool name, so this key is never blank. When the
        // provider gave no id the key IS the tool name while the handler looks up its own blank CurrentContext
        // .CallContent.CallId — it therefore misses and returns its fail-safe, which is the right degradation: a
        // provider that emits no call id gives the framework nothing to correlate on either, and a wrong answer is
        // worse than an honest "not collected".
        var stashKey = callId;

        if (!UserQuestionParser.TryParse((approvalRequest.ToolCall as FunctionCallContent)?.Arguments, out var questions, out var parseError))
        {
            // Never prompt an operator with unvalidated model output. Tell the MODEL its call was malformed and let it
            // retry properly; the operator sees nothing. The parse error is a fixed-shape structural sentence, so no
            // operator content and no raw model text reaches the log.
            _logger.LogInformation("Rejected a malformed {ToolName} call for invocation {InvocationId} without prompting the operator: {Reason}",
                AskUserTool.ToolName,
                package.InvocationId,
                parseError);
            _userQuestionAnswerStash.Stash(stashKey, UserQuestionResults.Unanswered(UserQuestionResults.MalformedCallReason, parseError));
            return "The question was not shown: the call's arguments were invalid.";
        }

        // An UNATTENDED run has nobody to show the question to, so skip the park and hand the model the same
        // "not answered" result the wait would have reached anyway — without the full MaxPendingToolCallAge idle that
        // every scheduled run reaching ask_user would otherwise pay before getting there.
        //
        // This is deliberately NOT what the approval path does, and the asymmetry must survive future tidying: an
        // unattended APPROVAL fails the turn immediately with a reason, because executing a tool nobody sanctioned is
        // not a safe default. An unattended QUESTION continues — the model asked for input it can proceed
        // without. Unifying the two would make every scheduled turn fail the moment its model happens to ask something.
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
            }).ConfigureAwait(false);

            // The hard cap on any human wait. Linked to the invocation token so a user cancel or shutdown still ends
            // the wait promptly; SetInvocationDeadline below is what stops the invocation's own (shorter) budget from
            // pre-empting this cap.
            using var questionTimeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            questionTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

            IReadOnlyList<UserQuestionAnswer> answers;
            setInvocationDeadline(true);
            try
            {
                answers = await questionCompletion.Task.WaitAsync(questionTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
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
            // The pending-question cap elapsed WITHOUT the invocation being cancelled: a genuine no-answer. Unlike the
            // approval path — which rethrows and fails the turn — the turn must continue instead, so this swallows the
            // timeout and hands the model an explicit "not answered" result.
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

        // Remember the decision for the rest of the conversation only when ALL of it lines up: the operator asked for
        // session scope, the decision is an APPROVE (a deny is never remembered — see ApprovalScope), and the request
        // was registered as memo-eligible when it was raised. The eligibility rules live entirely on that registration
        // side, so nothing posted to this endpoint can widen what gets remembered.
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

    // Whether this approval request has already been captured for the current segment. Prefers a namespaced stable key —
    // the tool-call CallId, else the approval's own RequestId — so a provider re-emitting the same request across
    // streamed chunks enqueues it once. A BLANK CallId must never bypass dedup (that would prompt N times and dangle N-1
    // ambiguous responses for a single call); when neither a CallId nor a RequestId is present, falls back to reference
    // identity so at least the same surfaced instance is not enqueued twice. `seenKeys` accumulates the keys already
    // captured this segment; a stable key is added to it here as a side effect on first sight.
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

    /// <summary>
    ///     Whether a framework-surfaced approval request belongs to <c>ask_user</c>. Matched on the tool NAME rather
    ///     than on any flag, because the name is the only thing that survives the framework's approval wrapping —
    ///     <c>ToolApprovalRequestContent.ToolCall</c> is the base type and the concrete
    ///     <see cref="FunctionCallContent" /> is what carries it.
    /// </summary>
    public static bool IsUserQuestionRequest(ToolApprovalRequestContent approvalRequest) =>
        string.Equals((approvalRequest.ToolCall as FunctionCallContent)?.Name, AskUserTool.ToolName, StringComparison.Ordinal);

    /// <summary>
    ///     The <see cref="ApprovalMemoKey" /> this approval request may be remembered under, or <see langword="null" />
    ///     when it is not eligible for a session-scoped approval at all. Everything about the memo's reach is decided
    ///     here:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 the operator's node-level always-prompt switch turns eligibility off entirely;
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 the tool must be one of MAF's two READ-ONLY skill tools. <c>run_skill_script</c> is excluded by
    ///                 this allow-list and must stay excluded — a durable approval on script execution is the one
    ///                 decision an operator should have to make every single time — and there is deliberately no
    ///                 "remember everything" mode for any other tool;
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 the named skill must be in this package's resolved set, which is what supplies the VERSION the
    ///                 approval is bound to. A skill the package does not carry cannot be remembered;
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 an IMPORTED skill is never eligible (see <see cref="ResolvedSkill" />);
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>read_skill_resource</c> must name the resource it wants, so one approval covers one resource
    ///                 rather than every resource the skill carries.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     The skill and resource names are only reachable by reading the model's own call arguments — the framework's
    ///     approval request carries the base <c>ToolCallContent</c>, and the concrete <see cref="FunctionCallContent" />
    ///     is what holds them.
    /// </summary>
    private ApprovalMemoKey? TryResolveSessionApprovalKey(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        string? toolName)
    {
        if (_skillSessionScopeDisabled || string.IsNullOrEmpty(toolName))
        {
            return null;
        }

        // Custom-tool branch, resolved BEFORE the skill-only guards (a custom tool is neither of MAF's two skill tools).
        // A custom tool is session-approvable ONLY when its mode is Fixed — a Fixed tool runs a verbatim, operator-authored
        // invocation the model cannot alter, so one "approve for session" grant is bounded. A Parameterized tool is
        // once-or-deny: it returns null here (never remembered) so every model-chosen argument set re-prompts. The memo is
        // bound to the tool's Version (mapped onto ApprovalMemoKey.SkillVersion) so a mid-conversation edit that bumps the
        // version invalidates the grant and re-prompts — mirroring the skill-version binding. ResourceName is null (a
        // custom tool has no sub-resource). A tool the package does not carry (a mid-turn delete) is not remembered.
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

#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI; the same scoped suppression the provider call sites use.
        var isResourceRead = string.Equals(toolName, AgentSkillsProvider.ReadSkillResourceToolName, StringComparison.Ordinal);
#pragma warning restore MAAI001

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

    // Resolves the audited category (from the offered tool's declared ToolCategory) and source (loopback vs hub) for a
    // resolved approval decision and hands them to the fire-and-forget-safe recorder. The recorder swallows every failure,
    // so this can never throw into — or stall — the approval round-trip.
    private async Task RecordApprovalDecisionAuditAsync(RuntimePackage package,
        string? toolName,
        string decision,
        long requestedTimestamp,
        CancellationToken cancellationToken)
    {
        var latencyMs = (long)Stopwatch.GetElapsedTime(requestedTimestamp).TotalMilliseconds;
        var category = ResolveApprovalToolCategory(package, toolName);
        var source = InvocationRunner.IsLocalLoopbackInvocation(package) ? ApprovalDecisionSources.Local : ApprovalDecisionSources.Hub;
        await _approvalAuditRecorder.RecordAsync(package.InvocationId,
            toolName ?? string.Empty,
            category,
            decision,
            source,
            latencyMs,
            cancellationToken).ConfigureAwait(false);
    }

    // The offered tool's declared risk category, matched by name against the package offer (AllowedToolDto.Category) —
    // the same categorized offer the policy layer evaluates, so no new plumbing is added just for the audit. Falls back to
    // Unknown when the tool is absent from the offer or unnamed, matching the fail-closed default the policy itself uses.
    // The provider-injected skill tools are checked FIRST because they are never in the offer at all (see
    // SkillToolCategories) and would otherwise audit as Unknown, making every skill approval indistinguishable in the
    // trail from a genuinely uncategorized tool.
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

    // A non-empty string argument off a function call, tolerating both the deserialized-string and the raw JsonElement
    // shapes providers hand the framework. Anything else (absent, null, a number, an object) yields null, which the
    // caller treats as "not eligible" — the memo fails closed on an argument it cannot read.
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
