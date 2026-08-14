namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Represents invocation runner.
/// </summary>
public sealed partial class InvocationRunner : IInvocationRunner
{
    private const string AgentToolCallFailureMessage = "Worker tool execution failed.";
    private const string ModelUnavailableMessage = "Selected model is not installed on this node.";
    private const string ProviderUnavailableMessage = "Provider unreachable.";

    // Surfaced when an endpoint's circuit breaker is open (recent consecutive transient failures): a fixed, path-free
    // message that tells the operator to retry shortly rather than reporting a hard provider outage.
    private const string ProviderTemporarilyUnavailableMessage = "Provider temporarily unavailable. Please retry shortly.";
    private const string OrchestrationFailureMessage = "Orchestration run failed.";
    private const string ModelDoesNotSupportThinkingMessage = "This model does not support reasoning.";

    private const string ModelDoesNotSupportToolsMessage = "This model does not support tool calling.";

    // Approval-decision audit labels for the two outcomes the runner reaches WITHOUT an operator round-trip. They
    // extend the ApprovalDecisions vocabulary (approve/deny/timeout) and read the same way in the audit trail; a
    // memo-suppressed approval is still audited precisely so session scope cannot thin the trail invisibly.
    // follow-up: fold both into ApprovalDecisions once the concurrent skills-import work on Client.Persistence lands.
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

    // llama-server failed to COMPILE the constrained-decoding grammar for the offered tool schemas ("Failed to
    // initialize samplers: failed to parse grammar"). The model is tool-capable — the schema set is what it could not
    // be prepared for — so this must never claim the model lacks tool calling. Fixed and path-free: the provider body
    // is never forwarded.
    private const string ToolCallingPreparationFailedMessage =
        "The model could not be prepared for tool calling with the current tool set. Retry with tools turned off, or select a different model.";

    // A provider HTTP 500 means the model was reached but failed to load OR run (e.g. an Ollama build too old for the
    // model architecture, or an out-of-memory at load). Phrased to cover both so it never falsely asserts a permanent
    // model defect, while still being far more actionable than the generic "Provider unreachable.".
    private const string ModelLoadFailedMessage = "The model could not be loaded or run on the provider.";

    // A "Local runtime default" send found no installed GGUF chat model to route to. Surfaced instead of the generic
    // "Provider unreachable." so the operator gets an actionable next step (pull a GGUF model) rather than a dead-end.
    private const string NoChatModelInstalledMessage = "No chat model installed. Pull a GGUF model to start chatting.";

    // A generic (non-inter-chunk) timeout: the invocation-level cancel-after or an HTTP client timeout. Its framework
    // message can name hosts/paths and is unbounded, so a fixed, path-free constant is surfaced in its place.
    private const string TimedOutMessage = "The operation timed out.";

    // A new local turn admitted after shutdown drain has begun. Surfaced as a clean Cancelled-category
    // failure — the node is going away — rather than being run into a drain that has already stopped waiting for it.
    private const string NodeDrainingMessage = "The node is shutting down and is not accepting new requests.";

    // The budgeter's hard-stop (see ApplyContextBudgetAsync): history still exceeds the resolved context budget after
    // the two-pass truncation. A fixed, path-free constant carrying no token counts, model names, or content.
    private const string ContextBudgetExceededMessage =
        "Conversation exceeds the model's context window even after truncation — Compact the conversation to summarize older messages, start a new chat, or switch to a larger-context model.";

    private static readonly Regex FrameworkExceptionNamePattern =
        new(@"\b(?:Microsoft|System)(?:\.[A-Za-z_][A-Za-z0-9_]*)*\.[A-Za-z_][A-Za-z0-9_]*Exception\b|\b(?:AgentException|ChatClientAgentException)\b", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _activeInvocationCompletions = new();

    private readonly IToolApprovalAuditRecorder _approvalAuditRecorder;
    private readonly IInvocationAttachmentTracker _attachmentTracker;
    private readonly ICapabilityReporter _capabilityReporter;
    private readonly IConversationContextBudgeter _contextBudgeter;
    private readonly ConversationContextBudgetOptions _contextBudgetOptions;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly string _defaultModel;
    private readonly IEnvelopeCryptoService _envelopeCryptoService;
    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;
    private readonly Lazy<IHubMessageSender> _hubSender;
    private readonly IInvocationAgentFactory _invocationAgentFactory;
    private readonly ILogger<InvocationRunner> _logger;
    private readonly TimeSpan _maxPendingToolCallAge;
    private readonly int _maxResponseSizeBytes;

    private readonly IOrchestrationAgentFactory _orchestrationAgentFactory;

    // Questions parked on the operator, keyed by the opaque request id the browser echoes back. Deliberately separate
    // from _pendingToolCalls: an approval resolves to a bool, a question resolves to the operator's answers, and
    // conflating them would let an approve/deny post release a question with no answer at all.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<UserQuestionAnswer>>> _pendingQuestions = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls = new(StringComparer.Ordinal);

    // Session-scoped approvals the operator explicitly granted (ApprovalScope.Session), used as a SET — the byte value
    // is ignored. Lives on the singleton runner, next to _pendingToolCalls/_pendingQuestions, because the memo has to
    // outlive the turn that created it: an approval agent is scoped to one invocation and could never span the
    // conversation. Never persisted, so a node restart forgets everything in here.
    private readonly ConcurrentDictionary<ApprovalMemoKey, byte> _sessionApprovals = new();

    // The memo key a currently-pending approval WOULD be remembered under, keyed by the approval request id. It is
    // written just before the request is broadcast, while the skill context is still in hand, and removed again by the
    // waiter. An entry exists ONLY for a memo-eligible request, which is what makes the eligibility rules — the two
    // read-only skill tools, a locally authored skill, session scope enabled — impossible to bypass from the resolve
    // side.
    private readonly ConcurrentDictionary<string, ApprovalMemoKey> _sessionApprovalCandidates = new(StringComparer.Ordinal);
    private readonly ProviderCallBudgetOptions _providerCallBudgetOptions;
    private readonly IProviderStreamResilience _providerStreamResilience;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IActiveCloudChatClientFactory _activeCloudFactory;
    private readonly ProviderResilienceOptions _resilienceOptions;
    private readonly IRuntimePackageValidator _runtimePackageValidator;

    // The operator's "skill tools always prompt" switch, read once at singleton construction off the composed node
    // approval policy (an operator edit applies on the next node restart, like the rest of that policy). Only the node
    // policy carries it: any other IToolApprovalPolicy — the AI.Agent permissive floor, a test double — leaves session
    // scope available, which is the pre-existing behaviour for every deployment that has not set the knob.
    private readonly bool _skillSessionScopeDisabled;

    private readonly SpawnOptions _spawnOptions;
    private readonly Lock _syncRoot = new();
    private readonly AgentToolPipelineOptions _toolPipelineOptions;

    // The effective tool-result wait budget for each active invocation, seeded from the package's
    // ToolCallTimeoutSeconds when RunAsync starts. ExecuteApiToolCallAsync (which only carries the invocation id) reads
    // it here so a package-scoped tool timeout wins over the node-global _maxPendingToolCallAge; absent an entry (a
    // tool call outside an active invocation) it falls back to the node-global age.
    private readonly ConcurrentDictionary<Guid, TimeSpan> _toolResultTimeoutsByInvocation = new();

    private readonly UserQuestionAnswerStash _userQuestionAnswerStash;

    private Guid? _currentInvocationId;

    // Set once (never reset) when shutdown drain begins, guarded by _syncRoot. A local invocation that reaches
    // admission after this is set is rejected: it registers AFTER the drain snapshot and would otherwise
    // become an untracked active run the drain never waits for.
    private bool _draining;

    // The caller/host token the active invocation's source is linked to (see RegisterActiveInvocation), captured so a
    // cancellation can be attributed to the caller rather than to the invocation watchdog WITHOUT relying on a token
    // callback: callbacks run in reverse registration order, so anything registered by the streaming agent after the
    // runner's own registration is released FIRST and can reach the failure mapping before an earlier callback ran.
    private CancellationToken _hostCancellationToken;

    private CancellationTokenSource? _invocationCancellationTokenSource;

    // The active turn's whole-turn budget, retained so the deadline can be RE-ARMED around a human round-trip
    // (see SetInvocationDeadline). Written and read only under _syncRoot, alongside the source it arms.
    private TimeSpan _invocationTimeout;

    // Whether the active turn is currently parked waiting on a human (a tool approval or an ask_user question).
    // Written and read only under _syncRoot. It exists so the AttachmentChanged handler can re-apply the deadline for a
    // park it did not itself start — a client re-attaching mid-park must get the full park budget back from that moment.
    private bool _parkedOnHuman;

    // Why the active invocation was DELIBERATELY cancelled, recorded synchronously under _syncRoot by the requester
    // itself (Cancel / CancelAll). Unknown means nobody asked: the cancellation then came from the invocation's own
    // CancelAfter watchdog or from the linked caller token, and both are read off observable state at mapping time.
    private CancellationOrigin _requestedCancellationOrigin;

    public InvocationRunner(Lazy<IHubMessageSender> hubSender,
        Lazy<IWorkerEventDispatcher> eventDispatcher,
        IInvocationAgentFactory invocationAgentFactory,
        IOrchestrationAgentFactory orchestrationAgentFactory,
        IEnvelopeCryptoService envelopeCryptoService,
        IRuntimePackageValidator runtimePackageValidator,
        ICapabilityReporter capabilityReporter,
        ILocalModelProviderResolver providerResolver,
        IActiveCloudChatClientFactory activeCloudFactory,
        IDeadLetterStore deadLetterStore,
        IProviderStreamResilience providerStreamResilience,
        IConversationContextBudgeter contextBudgeter,
        IOptions<ConversationContextBudgetOptions> contextBudgetOptions,
        IOptions<ProviderResilienceOptions> resilienceOptions,
        IOptions<AgentToolPipelineOptions> toolPipelineOptions,
        IOptions<ProviderCallBudgetOptions> providerCallBudgetOptions,
        IConfiguration configuration,
        INodeRuntimeSettings runtimeSettings,
        IOptions<SpawnOptions> spawnOptions,
        IToolApprovalAuditRecorder approvalAuditRecorder,
        IToolApprovalPolicy approvalPolicy,
        UserQuestionAnswerStash userQuestionAnswerStash,
        IInvocationAttachmentTracker attachmentTracker,
        ILogger<InvocationRunner> logger)
    {
        _hubSender = hubSender ?? throw new ArgumentNullException(nameof(hubSender));
        _approvalAuditRecorder = approvalAuditRecorder ?? throw new ArgumentNullException(nameof(approvalAuditRecorder));
        _userQuestionAnswerStash = userQuestionAnswerStash ?? throw new ArgumentNullException(nameof(userQuestionAnswerStash));
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _invocationAgentFactory = invocationAgentFactory ?? throw new ArgumentNullException(nameof(invocationAgentFactory));
        _orchestrationAgentFactory = orchestrationAgentFactory ?? throw new ArgumentNullException(nameof(orchestrationAgentFactory));
        _envelopeCryptoService = envelopeCryptoService ?? throw new ArgumentNullException(nameof(envelopeCryptoService));
        _runtimePackageValidator = runtimePackageValidator ?? throw new ArgumentNullException(nameof(runtimePackageValidator));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _activeCloudFactory = activeCloudFactory ?? throw new ArgumentNullException(nameof(activeCloudFactory));
        _deadLetterStore = deadLetterStore ?? throw new ArgumentNullException(nameof(deadLetterStore));
        _providerStreamResilience = providerStreamResilience ?? throw new ArgumentNullException(nameof(providerStreamResilience));
        _contextBudgeter = contextBudgeter ?? throw new ArgumentNullException(nameof(contextBudgeter));
        ArgumentNullException.ThrowIfNull(contextBudgetOptions);
        _contextBudgetOptions = contextBudgetOptions.Value;
        ArgumentNullException.ThrowIfNull(resilienceOptions);
        _resilienceOptions = resilienceOptions.Value;
        ArgumentNullException.ThrowIfNull(toolPipelineOptions);
        _toolPipelineOptions = toolPipelineOptions.Value;
        ArgumentNullException.ThrowIfNull(providerCallBudgetOptions);
        _providerCallBudgetOptions = providerCallBudgetOptions.Value;
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(spawnOptions);
        _spawnOptions = spawnOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The migrated default model + the response-size / pending-tool-call caps are read once at singleton
        // construction from INodeRuntimeSettings (stored > appsettings seed > default). The caps then live as plain
        // fields read on the hot streaming/cleanup loops, so an operator edit applies on the next process restart.
        // Ollama:ChatModel (an out-of-band runtime override, not a migrated setting) still wins over the
        // migrated default model when configured, mirroring the chat-connection fallback.
        _defaultModel = configuration.GetValue<string>("Ollama:ChatModel")
                        ?? runtimeSettings.GetDefaultModelName();
        _maxResponseSizeBytes = runtimeSettings.GetMaxResponseSizeMb() * 1024 * 1024;
        _maxPendingToolCallAge = TimeSpan.FromMinutes(runtimeSettings.GetMaxPendingToolCallAgeMinutes());

        // A concrete-type test rather than a widened IToolApprovalPolicy: the interface is the cross-project AI.Agent
        // contract for one call's yes/no verdict, and the node-only session-scope knob has no place on it. Any other
        // implementation leaves session scope available (today's behaviour).
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        _skillSessionScopeDisabled = approvalPolicy is NodeToolApprovalPolicy { SkillSessionScopeDisabled: true };

        // Subscribe for the process lifetime; both are singletons, so there is no unsubscribe path (mirrors
        // InvocationResumeRegistry's subscription to the same dispatcher).
        _attachmentTracker = attachmentTracker ?? throw new ArgumentNullException(nameof(attachmentTracker));
        _attachmentTracker.AttachmentChanged += OnAttachmentChanged;
    }

    public int ActiveInvocationCount => _activeInvocationCompletions.Count;

    public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var package = context.Package;

        // AUD4-23: mark the turn's processing start (baseline for the pre-spawn latency + TTFT metrics) and open a
        // coarse span for the whole turn, so the audited "silent pre-spawn gap" (a first send stalled several seconds
        // before the model spawn with zero log lines) surfaces as timed child spans rather than an apparent hang.
        var turnStartedTimestamp = Stopwatch.GetTimestamp();
        var harnessStartedTimestamp = context.HarnessStartedTimestamp ?? turnStartedTimestamp;
        using var turnActivity = NodeActivitySource.Source.StartActivity("chat.invocation.run");

        using (NodeActivitySource.Source.StartActivity("chat.invocation.validate_package"))
        {
            // Size cap OFF here: this package's context is the node's own stored history plus node-authored synthetic
            // context, and the inbound message was already capped at its entry seam (the chat hub for a local send, the
            // envelope assembler for a platform-dispatched one). Re-applying the cap per turn hard-failed every later
            // turn of a conversation that already held an oversized row — the conversation stayed unusable until the
            // user abandoned it. Oversized history is the context budgeter's problem below, and it trims it.
            var validationResult = _runtimePackageValidator.Validate(package, enforceMessageSizeCap: false);
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(string.Join("; ", validationResult.Errors));
            }
        }

        var sender = _hubSender.Value;
        var dispatcher = _eventDispatcher.Value;
        var shouldSendHubMessages = !IsLocalLoopbackInvocation(package);
        var sendEncrypted = shouldSendHubMessages && context.IsEncrypted;
        var sendPlain = shouldSendHubMessages && !context.IsEncrypted;

        // Resolved ONCE per turn from the package's TimeoutSettings plus the node-level operational options, then
        // flowed unchanged through both the single-agent and orchestration paths so the two enforce identical policy.
        // See TurnPolicy's XML doc for the composite budget (which timeout fires when).
        var turnPolicy = TurnPolicy.Resolve(package, _contextBudgetOptions, _resilienceOptions, _toolPipelineOptions, _maxPendingToolCallAge);

        RegisterActiveInvocation(package.InvocationId, turnPolicy.InvocationTimeout, cancellationToken);
        var activeInvocationCompletion = RegisterActiveInvocationCompletion(package.InvocationId, !shouldSendHubMessages);
        if (activeInvocationCompletion is null)
        {
            // Shutdown drain has started and this is a local turn admitted after the drain snapshot. Undo the
            // registration above and surface a clean, classified failure instead of running it into a drain that has
            // stopped waiting. A local turn sends no hub messages, so reporting to the dispatcher is the whole surface.
            ClearActiveInvocation(package.InvocationId);
            _logger.LogInformation("Rejecting local invocation {InvocationId}: the node is draining for shutdown.", package.InvocationId);
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, NodeDrainingMessage, FailureCategory.Cancelled).ConfigureAwait(false);
            return;
        }

        _toolResultTimeoutsByInvocation[package.InvocationId] = turnPolicy.ToolResultTimeout;

        using var providerBudgetScope = ProviderCallBudget.BeginScope(_providerCallBudgetOptions, harnessStartedTimestamp);
        var providerBudget = ProviderCallBudget.Current!;
        StreamState? stream = null;
        string? invocationOutcome = null;

        try
        {
            var invocationToken = GetInvocationCancellationToken();
            ModelResolution modelResolution;
            using (NodeActivitySource.Source.StartActivity("chat.invocation.resolve_model"))
            {
                modelResolution = await ResolveModelAsync(package.ModelProfile, invocationToken).ConfigureAwait(false);
            }

            var resolvedModel = modelResolution.Model;

            // Shared streaming state for both the single-agent and orchestration paths: the response/reasoning
            // accumulators, the byte caps, the monotonic sequence counters, and the terminal usage snapshot. Both
            // branches feed this through the same Emit* helpers so the transport, size cap, dispatcher reporting, and
            // ordering stay byte-for-byte identical.
            stream = new StreamState
            {
                HarnessStartedTimestamp = harnessStartedTimestamp
            };

            if (shouldSendHubMessages)
            {
                await sender.SendInvocationAcceptedAsync(package.InvocationId, invocationToken).ConfigureAwait(false);
            }

            var transport = new StreamTransport(this, sender, dispatcher, context, package, sendEncrypted, sendPlain);

            // Surface the silent model-substitution fallback (previously LogWarning-only) as a visible, non-fatal
            // chat notice now that the transport (and therefore the dispatcher) exists.
            if (modelResolution.Substituted)
            {
                await transport.EmitNoticeAsync(TurnNoticeKind.ModelSubstituted,
                    BuildModelSubstitutedNoticeMessage(modelResolution.RequestedModel, resolvedModel)).ConfigureAwait(false);
            }

            // Seed the per-root-invocation spawn context (Depth 0) for this turn so the spawn_subagent tool (when the
            // agent calls it) enforces the fan-out and cloud-spawn caps against ONE shared root. The context flows as an
            // AsyncLocal into the function-invocation pipeline that runs the tool body; disposal restores the prior
            // ambient value. A turn that never spawns pays only a struct allocation.
            using var spawnRoot = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);

            // Seed the active conversation id into the same root tool-loop scope so the AgentHome tool gateway can stage
            // this conversation's uploaded attachments into the sandbox. Like the spawn context it flows as an
            // AsyncLocal through the function-invocation pipeline; disposal restores the prior ambient value.
            using var conversationScope = AgentRunConversationContext.BeginScope(package.ConversationId);

            // AUD4-01: warm the local model to readiness BEFORE the watched streaming pull begins, so a cold big-model
            // load happens in its OWN size-aware window (owned by the supervisor) and is never killed by the shorter
            // stream-idle watchdog — the primary cause of the audited "big model can never load through chat" hang.
            // Cloud (Codex/Azure) and Ollama models are a no-op here. The load is decoupled from this caller's token in
            // the supervisor, so a user who cancels merely abandons the wait while the load continues in the background.
            var requestedContextTokens = turnPolicy.RequestedContextTokens ?? turnPolicy.ContextCapacityTokens;
            var localRuntime = await PrepareLocalRuntimeAsync(resolvedModel, dispatcher, package.InvocationId, stream, turnStartedTimestamp, invocationToken).ConfigureAwait(false);
            var effectiveContextTokens = localRuntime.EffectiveContextTokens;

            // AUD4-02: fold the launched effective context window into the turn policy so the OUTER conversation
            // budgeter sizes history against the real window rather than the configured default (see
            // TurnPolicy.WithEffectiveContext for the precedence). The same value is threaded into the agent definition
            // below so the INNER provider-round budgeter (num_ctx side channel) resolves the identical window.
            turnPolicy = turnPolicy.WithEffectiveContext(effectiveContextTokens);

            if (context.GenerationAdmissionPolicy is { } admissionPolicy)
            {
                var admissionContext = new InvocationGenerationAdmissionContext
                {
                    InvocationId = package.InvocationId,
                    RequestedContextTokens = requestedContextTokens,
                    EffectiveContextTokens = effectiveContextTokens,
                    ModelId = resolvedModel,
                    ProviderName = localRuntime.ProviderName
                };
                var decision = await admissionPolicy.EvaluateAsync(admissionContext, invocationToken).ConfigureAwait(false)
                               ?? throw new InvalidOperationException("The invocation generation admission policy returned no decision.");
                if (!decision.IsAllowed)
                {
                    throw new InvocationGenerationRejectedException(decision.SanitizedReason
                                                                    ?? "Invocation generation was rejected by policy.");
                }
            }

            // Branch: a package carrying a compiled orchestration spec drives the handoff workflow; everything else is
            // the unchanged single-agent loop. Both accumulate into `stream`, then share the completion block below.
            if (package.OrchestrationSpec is { } orchestrationSpec)
            {
                // The orchestration path's OUTER conversation budgeter sizes against the effective window via the updated
                // turnPolicy above. ORC-07: the turn's effective window is also threaded per participant so each
                // participant's INNER provider-round budgeter sizes against the window ITS model was launched with.
                await RunOrchestrationAsync(package, orchestrationSpec, resolvedModel, transport, stream, turnPolicy, effectiveContextTokens, invocationToken).ConfigureAwait(false);
            }
            else
            {
                await RunSingleAgentAsync(package, resolvedModel, transport, stream, turnPolicy, effectiveContextTokens, invocationToken).ConfigureAwait(false);
            }

            // Read the whole-turn wall-clock duration once. The same value rides every completion transport (encrypted
            // counts dict, plain payload, dispatcher report) so the persisted tokens-per-second is computed from one
            // authoritative measurement regardless of which path serves the turn.
            var generationDurationMs = (long)stream.GenerationStopwatch.Elapsed.TotalMilliseconds;

            // BE-01: emit cumulative model token usage from the single per-turn finalize point (NOT the per-tool-loop
            // usage-arrival site, which would double-count across rounds). Content-free — token counts tagged by the
            // coarse provider dimension, model id, and direction only.
            RecordTokenUsageMetric(stream, resolvedModel);

            if (sendEncrypted)
            {
                var tokenCounts = stream.UsageSnapshot?.ToTokenCounts() ?? new Dictionary<string, long>(StringComparer.Ordinal);
                tokenCounts["generationDurationMs"] = generationDurationMs;
                await sender.SendEncryptedCompletedAsync(_envelopeCryptoService.EncryptCompleted(package.ConversationId,
                        context.MessageId,
                        context.EpochVersion,
                        context.EpochKey.Span,
                        Encoding.UTF8.GetBytes(stream.ResponseBuilder.ToString()),
                        stream.Sequence,
                        tokenCounts,
                        stream.ReasoningBuilder.Length > 0 ? Encoding.UTF8.GetBytes(stream.ReasoningBuilder.ToString()) : null),
                    invocationToken).ConfigureAwait(false);
            }
            else if (sendPlain)
            {
                if (stream.UsageSnapshot is null)
                {
                    _logger.LogWarning("Terminal model usage was not reported for invocation {InvocationId} using model {ModelName}. Token fields will remain unknown.",
                        package.InvocationId,
                        resolvedModel);
                }

                await sender.SendReasoningStreamChunkAsync(package.InvocationId,
                    string.Empty,
                    isComplete: true,
                    stream.ReasoningSequence + 1,
                    invocationToken).ConfigureAwait(false);
                await sender.SendTokenStreamChunkAsync(package.InvocationId,
                    string.Empty,
                    isComplete: true,
                    stream.Sequence + 1,
                    invocationToken).ConfigureAwait(false);
                await sender.SendInvocationCompletedAsync(new InvocationCompletedPayload
                {
                    InvocationId = package.InvocationId,
                    FinalContent = stream.ResponseBuilder.ToString(),
                    ModelUsed = resolvedModel,
                    InputTokens = stream.UsageSnapshot?.InputTokens,
                    OutputTokens = stream.UsageSnapshot?.OutputTokens,
                    TokensUsed = stream.UsageSnapshot?.TotalTokens,
                    FinalReasoning = stream.ReasoningBuilder.ToString(),
                    ReasoningTokens = stream.UsageSnapshot?.ReasoningTokens,
                    GenerationDurationMs = generationDurationMs
                }, invocationToken).ConfigureAwait(false);
            }

            await dispatcher.ReportInvocationCompletedAsync(package.InvocationId,
                stream.UsageSnapshot?.InputTokens,
                stream.UsageSnapshot?.OutputTokens,
                stream.UsageSnapshot?.TotalTokens,
                stream.UsageSnapshot?.ReasoningTokens,
                generationDurationMs).ConfigureAwait(false);
            invocationOutcome = "completed";
        }
        catch (OperationCanceledException) when (IsCurrentInvocation(package.InvocationId))
        {
            invocationOutcome = "cancelled";
            CancelPendingToolCalls(package.InvocationId);
            var cancellationOrigin = ResolveCancellationOrigin();
            var failureCategory = ClassifyCancellation(cancellationOrigin);
            // AUD4-19: count the cancellation by its cause (user | watchdog | shutdown). Distinct from InvocationFailedTotal:
            // a cancel is an outcome, not a failure. An invocation-level timeout ("watchdog") is additionally surfaced as a
            // Timeout failure below via ReportInvocationFailedAsync — the two metrics answer different questions.
            NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", ClassifyCancellationMetricCategory(cancellationOrigin)));
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, "Invocation timed out or was cancelled", failureCategory).ConfigureAwait(false);
            if (shouldSendHubMessages)
            {
                await TrySendFailureAsync(sender, context, "Invocation timed out or was cancelled", failureCategory).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            invocationOutcome = exception is LlamaServerModelEjectedException ? "cancelled" : "failed";
            _logger.LogError(exception, "Invocation {InvocationId} failed.", package.InvocationId);
            var (failureCategory, message) = MapFailure(exception);
            // An operator force-eject surfaces as a Cancelled-category LlamaServerModelEjectedException here (not the OCE
            // path). Count it as a cancellation cause rather than a failure, mirroring the OCE branch above.
            if (exception is LlamaServerModelEjectedException)
            {
                NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", "operator_eject"));
            }

            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, message, failureCategory).ConfigureAwait(false);
            if (shouldSendHubMessages)
            {
                await TrySendFailureAsync(sender, context, message, failureCategory).ConfigureAwait(false);
            }
        }
        finally
        {
            var efficiencyRecord = new InvocationEfficiencyRecord(package.InvocationId,
                invocationOutcome ?? "failed",
                stream?.ProviderTag ?? "unknown",
                package.OrchestrationSpec is not null,
                Stopwatch.GetElapsedTime(harnessStartedTimestamp).TotalMilliseconds,
                context.PreRunDurationMs,
                context.QueueDurationMs,
                stream?.ModelReadinessDurationMs,
                stream?.FirstOutputLatencyMs,
                stream?.UsageSnapshot?.InputTokens,
                stream?.UsageSnapshot?.OutputTokens,
                stream?.UsageSnapshot?.ReasoningTokens,
                providerBudget.CaptureEfficiencySnapshot());
            TryRecordInvocationEfficiency(efficiencyRecord, turnActivity);

            CleanupStaleToolCalls(_maxPendingToolCallAge);
            _toolResultTimeoutsByInvocation.TryRemove(package.InvocationId, out _);
            ClearActiveInvocation(package.InvocationId);
            CompleteActiveInvocation(package.InvocationId, activeInvocationCompletion);
            await TryReportCapabilitiesAfterInvocationAsync(package.InvocationId).ConfigureAwait(false);
        }
    }

    public async Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Fence local admission and snapshot the active set ATOMICALLY under _syncRoot: a new local turn either
        // registered its completion before this lock (so it is in the snapshot and awaited) or hits admission after and
        // is rejected (RegisterActiveInvocationCompletion returns null). No local turn can slip into the gap between the
        // fence and the snapshot and become an untracked active run.
        Task[] activeInvocationTasks;
        lock (_syncRoot)
        {
            _draining = true;
            activeInvocationTasks = _activeInvocationCompletions.Values.Select(static completion => completion.Task).ToArray();
        }

        if (activeInvocationTasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(activeInvocationTasks).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Cancel(Guid invocationId)
    {
        CancelCore(invocationId, CancellationOrigin.User);
    }

    public void CancelDetached(Guid invocationId)
    {
        CancelCore(invocationId, CancellationOrigin.DetachedGraceExpired);
    }

    private void CancelCore(Guid invocationId, CancellationOrigin origin)
    {
        CancellationTokenSource? invocationCancellationTokenSource = null;

        lock (_syncRoot)
        {
            if (_currentInvocationId == invocationId)
            {
                invocationCancellationTokenSource = _invocationCancellationTokenSource;
                _requestedCancellationOrigin = origin;
            }
        }

        invocationCancellationTokenSource?.Cancel();
        CancelPendingToolCalls(invocationId);
    }

    public void CancelAll()
    {
        CancellationTokenSource? invocationCancellationTokenSource;

        lock (_syncRoot)
        {
            invocationCancellationTokenSource = _invocationCancellationTokenSource;

            // An external stop of everything in flight (the hub's disconnect request), NOT the invocation watchdog:
            // record it here so the turn is classified as a shutdown-style cancellation rather than a timeout.
            if (invocationCancellationTokenSource is not null && _requestedCancellationOrigin == CancellationOrigin.Unknown)
            {
                _requestedCancellationOrigin = CancellationOrigin.Shutdown;
            }
        }

        invocationCancellationTokenSource?.Cancel();

        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                removedPendingToolCall.ApprovalCompletion.TrySetCanceled(CancellationToken.None);
                removedPendingToolCall.ResultCompletion.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    public void CleanupStaleToolCalls(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;

        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (pendingToolCall.Value.CreatedAt >= cutoff)
            {
                continue;
            }

            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                var timeoutException = new TimeoutException("Tool call timed out during cleanup.");
                removedPendingToolCall.ApprovalCompletion.TrySetException(timeoutException);
                removedPendingToolCall.ResultCompletion.TrySetException(timeoutException);
            }
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

    public void ResolveToolCallResult(ToolCallResultEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_pendingToolCalls.TryRemove(evt.RequestId, out var pendingToolCall))
        {
            pendingToolCall.ResultCompletion.TrySetResult(evt);
        }
    }

    public Task<string> ExecuteApiToolCallAsync(Guid invocationId,
        string toolName,
        string parameters,
        CancellationToken cancellationToken = default)
    {
        // Default to the approval-gated path; the per-tool overload below is what BuildInvocationTools wires in,
        // passing the tool's RequiresApproval flag so non-approval tools auto-execute.
        return ExecuteApiToolCallAsync(invocationId, toolName, parameters, requiresApproval: true, cancellationToken);
    }

    /// <summary>
    ///     AUD4-01 model-readiness phase: warms a LOCAL (llama.cpp) model to readiness BEFORE the stream-idle watchdog
    ///     is armed, so a cold big-model load runs in its own size-aware window (owned by the supervisor) instead of
    ///     being killed by the shorter no-first-chunk watchdog. Cloud (Codex/Azure) and Ollama models route elsewhere or
    ///     warm cheaply on first send, so they are a no-op here. Surfaces the phase into the invocation state (so the UI
    ///     can render "loading model…") and records readiness timing/outcome on <see cref="NodeMetrics" />.
    ///     <para>
    ///         INVARIANT: the warm await is decoupled from the model load's lifetime in the supervisor — a caller
    ///         cancellation abandons THIS wait (rethrown so the turn terminates as cancelled) while the load continues in
    ///         the background and the model becomes warm for the next send. A warm FAILURE is swallowed here so the
    ///         streaming send surfaces the real, classified error through its normal path rather than a duplicate here.
    ///     </para>
    /// </summary>
    private async Task<LocalRuntimePreparationResult> PrepareLocalRuntimeAsync(string resolvedModel,
        IWorkerEventDispatcher dispatcher,
        Guid invocationId,
        StreamState stream,
        long turnStartedTimestamp,
        CancellationToken cancellationToken)
    {
        var provider = await ResolveWarmableProviderAsync(resolvedModel, invocationId, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            // No local cold-load for this turn (cloud, Ollama, or an unresolved provider): the TTFT baseline is turn
            // start and the provider dimension is "remote" — the first-token latency is the provider's own, not a
            // measurable local warm. The send-to-load-start histogram is deliberately local-only, so it is not recorded.
            stream.ProviderTag = "remote";
            stream.ModelReadyTimestamp = turnStartedTimestamp;
            return new LocalRuntimePreparationResult(EffectiveContextTokens: null, ProviderName: null);
        }

        // AUD4-19: this turn pays a real local cold-load. Record how long the turn waited before the load even began
        // (the audited silent pre-spawn gap), tag the provider dimension "local", and measure TTFT from readiness.
        stream.ProviderTag = "local";
        NodeMetrics.TurnToModelLoadStartMs.Record(Stopwatch.GetElapsedTime(turnStartedTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("provider", "local"));

        // Phase: preparing runtime → loading model. Both fire BEFORE the stream-idle watchdog is armed.
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.PreparingRuntime).ConfigureAwait(false);
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.LoadingModel).ConfigureAwait(false);
        _logger.LogInformation("Warming local model for invocation {InvocationId} before streaming (readiness decoupled from the stream-idle watchdog).", invocationId);

        using var readinessActivity = NodeActivitySource.Source.StartActivity("chat.invocation.model_readiness");
        var startedUtc = DateTimeOffset.UtcNow;
        try
        {
            await provider.WarmModelAsync(resolvedModel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled its WAIT (the detached load continues in the supervisor and warms the model for the
            // next send). Record the abandoned readiness and rethrow so the turn terminates as cancelled.
            stream.ModelReadinessDurationMs = RecordReadiness(startedUtc, "cancelled");
            readinessActivity?.SetTag("outcome", "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            // Readiness failed (e.g. model incompatible / OOM). Record it and let the streaming send surface the real,
            // classified failure — it hits the same supervisor path and produces the proper error.
            stream.ModelReadinessDurationMs = RecordReadiness(startedUtc, "failed");
            readinessActivity?.SetTag("outcome", "failed");
            _logger.LogWarning(exception, "Model warm failed for invocation {InvocationId}; the streaming send will surface the classified failure.", invocationId);
            return new LocalRuntimePreparationResult(EffectiveContextTokens: null, provider.ProviderName);
        }

        var durationMs = RecordReadiness(startedUtc, "ready");
        stream.ModelReadinessDurationMs = durationMs;
        readinessActivity?.SetTag("outcome", "ready");

        // The model is ready: measure TTFT from HERE (the first emitted chunk records against this baseline).
        stream.ModelReadyTimestamp = Stopwatch.GetTimestamp();
        _logger.LogInformation("Local model ready for invocation {InvocationId} after {ElapsedMs:F0} ms; arming the stream-idle watchdog for generation.", invocationId, durationMs);

        // Phase: generating (the model is ready; streaming begins under the stream-idle watchdog).
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.Generating).ConfigureAwait(false);

        // AUD4-02: with the model now ready, read the effective per-slot context window it actually loaded so the turn's
        // budgeters + the num_ctx side channel size against the REAL window (llama.cpp's -c) rather than the app default.
        // Best-effort — a null here just keeps the configured default. A cancellation propagates (the turn is terminating).
        var effectiveContextTokens = await ResolveEffectiveContextTokensAsync(provider, resolvedModel, invocationId, cancellationToken).ConfigureAwait(false);
        return new LocalRuntimePreparationResult(effectiveContextTokens, provider.ProviderName);
    }

    private readonly record struct LocalRuntimePreparationResult(int? EffectiveContextTokens, string? ProviderName);

    /// <summary>
    ///     Reads the launched effective context window for <paramref name="resolvedModel" /> from the warm local provider
    ///     (llama.cpp). Returns <see langword="null" /> when unknown (the runtime does not report one, or the read failed)
    ///     so the caller keeps the configured default. A cancellation propagates.
    /// </summary>
    private async Task<int?> ResolveEffectiveContextTokensAsync(ILocalModelProvider provider,
        string resolvedModel,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var runtimeInfo = await provider.GetRuntimeInfoAsync(resolvedModel, cancellationToken).ConfigureAwait(false);
            return runtimeInfo is { EffectiveContextTokens: > 0 } info ? info.EffectiveContextTokens : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Reading the effective context window for invocation {InvocationId} failed; the configured default window is used.", invocationId);
            return null;
        }
    }

    /// <summary>
    ///     Resolves the local provider that serves <paramref name="resolvedModel" />, returning it only when it is the
    ///     llama.cpp runtime (the one that pays a real cold-load before a watched stream). Any other provider (Ollama /
    ///     cloud) or a resolution failure returns <see langword="null" /> so the warm phase is skipped; a genuine
    ///     cancellation propagates.
    /// </summary>
    private async Task<ILocalModelProvider?> ResolveWarmableProviderAsync(string resolvedModel, Guid invocationId, CancellationToken cancellationToken)
    {
        // A cloud-routed model (Codex/Azure) must never trigger a local warm. The provider resolver maps any UNMAPPED
        // model name to the default local provider (llamacpp), so a cloud model id like "gpt-5.6-terra" would otherwise
        // resolve to llama-server and fail its cold-load with "model not installed". This is the SAME per-request routing
        // decision RuntimeChatClient makes for the send, so warm and send stay consistent (see IsCloudProviderSelected).
        if (_activeCloudFactory.IsCloudProviderSelected(resolvedModel))
        {
            return null;
        }

        try
        {
            var provider = await _providerResolver.ResolveProviderForModelAsync(resolvedModel, cancellationToken).ConfigureAwait(false);
            return provider is not null && string.Equals(provider.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
                ? provider
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Skipping runtime warm for invocation {InvocationId}: could not resolve a local provider for the model.", invocationId);
            return null;
        }
    }

    /// <summary>
    ///     Emits the terminal token-usage counter for a completed turn (BE-01). Called once from the shared completion
    ///     block — never the per-tool-loop usage-arrival site — so a multi-round tool run counts its final usage exactly
    ///     once. No-op when the model reported no usage. Content-free: only the coarse provider dimension
    ///     (<see cref="StreamState.ProviderTag" />, local | remote), the resolved model id, and the direction tag ride the
    ///     metric — never any prompt/completion text.
    /// </summary>
    private static void RecordTokenUsageMetric(StreamState stream, string resolvedModel)
    {
        if (stream.UsageSnapshot is not { } usage)
        {
            return;
        }

        RecordTokenDirection(stream.ProviderTag, resolvedModel, "input", usage.InputTokens);
        RecordTokenDirection(stream.ProviderTag, resolvedModel, "output", usage.OutputTokens);
    }

    private static void RecordTokenDirection(string provider, string model, string direction, int? tokens)
    {
        // Skip a null/zero direction so an unreported field adds no zero-valued time series.
        if (tokens is not > 0)
        {
            return;
        }

        NodeMetrics.ModelTokenUsageTotal.Add(tokens.Value,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("direction", direction));
    }

    private void TryRecordInvocationEfficiency(InvocationEfficiencyRecord record, Activity? activity)
    {
        try
        {
            InvocationEfficiencyTelemetry.Record(record, activity, _logger);
        }
        catch (Exception exception)
        {
            // Observability must never replace the invocation's real outcome or skip the cleanup that follows this call.
            // Keep the fallback content-free: no record values or user/model/tool data are echoed here.
            _logger.LogTrace(exception, "Agent harness efficiency telemetry could not be emitted.");
        }
    }

    /// <summary>Records the model-readiness duration + outcome on <see cref="NodeMetrics" /> and returns the elapsed milliseconds.</summary>
    private static double RecordReadiness(DateTimeOffset startedUtc, string outcome)
    {
        var durationMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
        NodeMetrics.ModelReadinessDurationMs.Record(durationMs, new KeyValuePair<string, object?>("outcome", outcome));
        NodeMetrics.ModelReadinessTotal.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        return durationMs;
    }

    // The single-agent path. Drives one ChatClientAgent over an approval-gated do/while loop, accumulating into
    // `stream` through the shared transport so the streaming behavior matches the orchestration path byte-for-byte.
    private async Task RunSingleAgentAsync(RuntimePackage package,
        string resolvedModel,
        StreamTransport transport,
        StreamState stream,
        TurnPolicy turnPolicy,
        int? effectiveContextTokens,
        CancellationToken invocationToken)
    {
        // Deterministic input-context budgeting is applied at BOTH history growth points so a long conversation (or a
        // long tool-calling loop) never overruns the window the provider is launched with. The gate gets both the
        // "logged once" and "notice emitted once" flags so an invocation logs/notifies at most once regardless of how
        // many rounds trim, and throws (see ApplyContextBudgetAsync) the first time truncation still leaves history
        // over budget.
        var budgetGate = new ContextBudgetNoticeGate();

        // Built once for the whole turn: the offer list is fixed for the invocation, and the budgeter's framing memo is
        // keyed on these string instances (see ApplyContextBudgetAsync).
        var toolBudgetDefinitions = BuildToolBudgetDefinitions(package);
        var seededMessages = await ApplyContextBudgetAsync(BuildChatMessages(package), package, toolBudgetDefinitions, resolvedModel, "initial-assembly", turnPolicy, transport, budgetGate)
            .ConfigureAwait(false);

        var definition = BuildInvocationDefinition(package, resolvedModel, seededMessages, effectiveContextTokens);
        // Coarse span over the MAF agent build (AUD4-23) — another pre-first-token stage. Disposed right after the
        // build so it does not enclose the streaming loop; the agent context keeps its normal await-using scope.
        var buildAgentActivity = NodeActivitySource.Source.StartActivity("chat.invocation.build_agent");
        await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken).ConfigureAwait(false);
        buildAgentActivity?.Dispose();

        // Maps callId → the tool name plus what its Requested event already carried, so FunctionResultContent (which has
        // no Name) can resolve the tool name from the earlier FunctionCallContent with the matching CallId, and so a
        // re-emitted FunctionCallContent can be recognised as a repeat before it pays another serialize + dispatch.
        var pendingLocalToolCalls = new Dictionary<string, (string Name, object? Arguments, string? SerializedArguments)>(StringComparer.Ordinal);

        // Tracks which tools this turn has already surfaced a ToolDisabled notice for, so a model that keeps calling a
        // disabled tool (each further call short-circuits to the same "tool_disabled" result — see
        // ToolArgumentRepairAIFunction) is reported to the chat exactly once per tool, not once per call.
        var notifiedDisabledTools = new HashSet<string>(StringComparer.Ordinal);

        // The conversation grows across approval-gated segments. A high-risk ClientLocal tool wrapped in
        // ApprovalRequiredAIFunction makes FunctionInvokingChatClient surface a ToolApprovalRequestContent and
        // end the segment WITHOUT executing the tool. We carry the decision over the existing approval transport
        // and resume threadlessly (session: null) by replaying the folded segment messages plus the approval
        // response. A segment that surfaces no approval request completes the run.
        var currentMessages = new List<ChatMessage>(agentContext.SeedMessages);

        // A single model turn can surface MORE than one approval request (a parallel-tool-call turn wrapping two
        // approval-gated tools). Collect EVERY request in the segment, deduped so a provider re-emitting the same request
        // across streamed chunks enqueues it once — none is lost (the scalar this replaced kept only the last, dangling
        // the earlier requests forever). The dedup key is namespaced so a CallId and an approval Id can never
        // collide across two different requests.
        var pendingApprovals = new List<ToolApprovalRequestContent>();
        var pendingApprovalKeys = new HashSet<string>(StringComparer.Ordinal);

        // Inter-chunk idle bound for every segment: if the provider stalls between streamed chunks for longer than the
        // resolved policy's stream-idle timeout the watchdog cancels the send and surfaces a distinct (Timeout-category)
        // failure. A non-positive value disables it (the validator already rejects one for a real package).
        var streamIdleTimeout = turnPolicy.StreamIdleTimeout;
        var streamIdleTimeoutMessage = turnPolicy.StreamIdleTimeoutMessage;

        // The pre-first-token retry + circuit breaker only guards the FIRST segment's send: once any chunk has streamed
        // (or a later approval-resume segment begins, which by definition follows earlier output) a retry could
        // duplicate output, so subsequent segments run the provider send directly.
        var isFirstSegment = true;

        // The per-segment update list below is retained ONLY to replay a folded segment when a tool-approval request
        // surfaces. A ToolApprovalRequestContent can originate from nothing but an ApprovalRequiredAIFunction, and
        // InvocationToolResolver's effective policy for a resolved tool is "registry pre-wrap OR offer flag" — where the
        // registry pre-wrap (ClientLocalToolRegistry's handler default, MCP's always-on default) is already ORed into
        // this DTO flag by the resolver's node-policy compose, which is tighten-only. So an offer of ClientLocal tools
        // that all carry RequiresApproval=false can never wrap one, and every streamed update of that (common) turn is
        // retained for nothing. Any OTHER location reaches the resolver as a bridged function carrying no approval
        // metadata, which the resolver treats as fail-closed — so it counts as possible here too.
        var approvalPossible = package.AllowedTools.Any(static tool => tool.RequiresApproval || tool.Location != ToolLocation.ClientLocal);

        do
        {
            // Growth point (b): before each provider round, re-budget the (approval-)grown message list. On the first
            // iteration this is a cheap passthrough (the seed was already budgeted); on an approval resume it bounds the
            // folded tool-call + approval history. The protected recent turns — which carry the in-flight round — are
            // never trimmed, so a budgeted list is still valid to send.
            var budgetedMessages = await ApplyContextBudgetAsync(currentMessages, package, toolBudgetDefinitions, resolvedModel, "tool-loop", turnPolicy, transport, budgetGate).ConfigureAwait(false);
            if (!ReferenceEquals(budgetedMessages, currentMessages))
            {
                currentMessages = budgetedMessages as List<ChatMessage> ?? [.. budgetedMessages];
            }

            pendingApprovals.Clear();
            pendingApprovalKeys.Clear();
            var segmentUpdates = new List<AgentResponseUpdate>();

            // Each provider send is guarded by the inter-chunk idle watchdog (the watchdog owns the token the provider
            // call binds cancellation to, so an idle expiry actually cancels the send). The first segment additionally
            // runs through the pre-first-token retry + circuit breaker; the retry re-invokes this whole factory, so a
            // fresh idle watchdog guards every attempt.
            IAsyncEnumerable<AgentResponseUpdate> ProviderSend(CancellationToken sendToken)
            {
                return StreamIdleWatchdog.WithIdleTimeout(innerToken => agentContext.Agent.RunStreamingAsync(currentMessages, session: null, agentContext.RunOptions, innerToken),
                    streamIdleTimeout,
                    streamIdleTimeoutMessage,
                    sendToken);
            }

            var segmentStream = isFirstSegment
                ? _providerStreamResilience.ExecuteStreamingAsync(resolvedModel, ProviderSend, invocationToken)
                : ProviderSend(invocationToken);

            await foreach (var update in segmentStream.ConfigureAwait(false))
            {
                if (approvalPossible)
                {
                    segmentUpdates.Add(update);
                }

                var textChunk = update.Text;

                // Reasoning text and the terminal usage snapshot are pulled in the SAME pass that fires the tool-call
                // lifecycle events, rather than the three separate OfType/Concat/LastOrDefault scans this ran per
                // streamed token. Local (ClientSide) tools execute via FunctionInvokingChatClient and never reach
                // ExecuteApiToolCallAsync, so detecting FunctionCallContent / FunctionResultContent here is what keeps
                // their lifecycle events on the SSE stream. Updates with no Contents (a plain text-only token) skip the
                // loop entirely.
                StringBuilder? thinkingBuilder = null;
                UsageDetails? usage = null;

                if (update.Contents is { Count: > 0 } contents)
                {
                    foreach (var content in contents)
                    {
                        switch (content)
                        {
                            case TextReasoningContent reasoning:
                                (thinkingBuilder ??= new StringBuilder()).Append(reasoning.Text);
                                break;

                            case UsageContent usageContent:
                                // LastOrDefault semantics: the last usage content in the update wins.
                                usage = usageContent.Details;
                                break;

                            case FunctionCallContent functionCall:
                                var callId = ResolveToolCallCardId(functionCall.CallId, functionCall.Name);

                                // A provider that re-emits the SAME call across streamed chunks would otherwise pay a
                                // fresh Serialize + dispatch + SignalR frame per repeat — and, worse, each repeat is
                                // appended to InvocationResumeRegistry's CAPPED tool history, where it evicts a real
                                // event. Both guards below are conservative: a genuinely distinct call, or the same call
                                // id whose arguments changed, still reports exactly as before.
                                var isRepeatedCall = pendingLocalToolCalls.TryGetValue(callId, out var alreadyRequested)
                                                     && string.Equals(alreadyRequested.Name, functionCall.Name, StringComparison.Ordinal);

                                // Same content instance re-emitted: identical by construction, so skip the serialize too.
                                if (isRepeatedCall && ReferenceEquals(alreadyRequested.Arguments, functionCall.Arguments))
                                {
                                    break;
                                }

                                var serializedArguments = functionCall.Arguments is not null
                                    ? JsonSerializer.Serialize(functionCall.Arguments)
                                    : null;

                                // Distinct instance, byte-identical payload: the event would be indistinguishable from
                                // the one already on the wire. Cache the new instance so the next repeat takes the
                                // cheaper reference check above.
                                if (isRepeatedCall && string.Equals(alreadyRequested.SerializedArguments, serializedArguments, StringComparison.Ordinal))
                                {
                                    pendingLocalToolCalls[callId] = (alreadyRequested.Name, functionCall.Arguments, alreadyRequested.SerializedArguments);
                                    break;
                                }

                                pendingLocalToolCalls[callId] = (functionCall.Name, functionCall.Arguments, serializedArguments);

                                await transport.Dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
                                {
                                    InvocationId = package.InvocationId,
                                    ToolCallId = callId,
                                    ToolName = functionCall.Name,
                                    Phase = ToolCallLifecyclePhase.Requested,
                                    Arguments = serializedArguments,
                                    RequiresApproval = false
                                }).ConfigureAwait(false);
                                break;

                            case FunctionResultContent functionResult:
                                var resultCallId = functionResult.CallId ?? string.Empty;
                                var toolName = pendingLocalToolCalls.TryGetValue(resultCallId, out var requested)
                                    ? requested.Name
                                    : resultCallId;
                                var toolResultText = functionResult.Result?.ToString();

                                await transport.Dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
                                {
                                    InvocationId = package.InvocationId,
                                    ToolCallId = resultCallId,
                                    ToolName = toolName,
                                    Phase = ToolCallLifecyclePhase.Completed,
                                    Result = toolResultText,
                                    IsError = functionResult.Exception is not null
                                }).ConfigureAwait(false);

                                // ToolArgumentRepairAIFunction returns this structured result (rather than throwing)
                                // once a tool is disabled for the rest of the run after repeated invalid-argument
                                // calls — a silent behavior previously visible only in the tool-result JSON the model
                                // sees. Surface it to the chat once per tool (further calls to the same disabled tool
                                // return the identical marker every time).
                                if (IsToolDisabledResult(toolResultText) && notifiedDisabledTools.Add(toolName))
                                {
                                    await transport.EmitNoticeAsync(TurnNoticeKind.ToolDisabled,
                                        BuildToolDisabledNoticeMessage(toolName),
                                        toolName).ConfigureAwait(false);
                                }

                                break;

                            case ToolApprovalRequestContent approvalRequest:
                                // FunctionInvokingChatClient surfaces this for an ApprovalRequiredAIFunction instead of
                                // executing the tool. Capture EVERY request in the segment, deduped (the same request can
                                // be re-emitted across streamed chunks); the segment ends and the outer loop runs each
                                // approval round-trip, then resumes threadlessly with the decisions.
                                if (!IsDuplicatePendingApproval(approvalRequest, pendingApprovals, pendingApprovalKeys))
                                {
                                    pendingApprovals.Add(approvalRequest);
                                }

                                break;
                        }
                    }
                }

                if (usage is not null)
                {
                    stream.UsageSnapshot = UsageSnapshot.From(usage);
                    _logger.LogDebug("Received terminal usage for invocation {InvocationId}: input={InputTokens}, output={OutputTokens}, reasoning={ReasoningTokens}, total={TotalTokens}.",
                        package.InvocationId,
                        stream.UsageSnapshot.InputTokens,
                        stream.UsageSnapshot.OutputTokens,
                        stream.UsageSnapshot.ReasoningTokens,
                        stream.UsageSnapshot.TotalTokens);
                }

                if (thinkingBuilder is { Length: > 0 })
                {
                    await transport.EmitReasoningAsync(stream, thinkingBuilder.ToString(), invocationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(textChunk))
                {
                    continue;
                }

                await transport.EmitTextAsync(stream, textChunk, invocationToken).ConfigureAwait(false);
            }

            // The first segment has drained; any resume segment past this point follows earlier output and must not be
            // retried (a retry there would replay already-streamed chunks).
            isFirstSegment = false;

            if (pendingApprovals.Count > 0)
            {
                // Fold the streamed segment into messages (carries the assistant tool-call(s) + approval request(s)),
                // run EACH approval round-trip over the existing transport (the transport presents one at a time — the
                // approvals resolve in turn), then replay history + ONE user message carrying every approval response so
                // FunctionInvokingChatClient reconstructs and executes (or rejects) all the pending tool calls. Multiple
                // ToolApprovalResponseContent may share a single user ChatMessage.
                var foldedMessages = segmentUpdates.ToAgentResponse().Messages;
                currentMessages.AddRange(foldedMessages);

                var approvalResponses = new List<AIContent>(pendingApprovals.Count);
                foreach (var approvalRequest in pendingApprovals)
                {
                    // ask_user rides the approval seam for its BLOCKING behaviour, not for a risk verdict (see
                    // AskUserToolHandler). Its round-trip collects an ANSWER and then always approves, so the framework
                    // executes the tool and the handler returns the stashed answer as the tool result. Every other tool
                    // keeps the unchanged approve/deny path.
                    if (IsUserQuestionRequest(approvalRequest))
                    {
                        var answerNote = await RequestUserAnswerAsync(package, approvalRequest, invocationToken).ConfigureAwait(false);
                        approvalResponses.Add(approvalRequest.CreateResponse(approved: true, answerNote));
                        continue;
                    }

                    var approved = await RequestToolApprovalAsync(package, approvalRequest, invocationToken).ConfigureAwait(false);
                    approvalResponses.Add(approvalRequest.CreateResponse(approved, approved ? "Approved by user." : "Rejected by user."));
                }

                currentMessages.Add(new ChatMessage(ChatRole.User, approvalResponses));
            }
        } while (pendingApprovals.Count > 0);
    }

    // Whether this approval request has already been captured for the current segment. Prefers a namespaced stable key —
    // the tool-call CallId, else the approval's own RequestId — so a provider re-emitting the same request across
    // streamed chunks enqueues it once. A BLANK CallId must never bypass dedup (that would prompt N times and dangle N-1
    // ambiguous responses for a single call); when neither a CallId nor a RequestId is present, falls back to reference
    // identity so at least the same surfaced instance is not enqueued twice. `seenKeys` accumulates the keys already
    // captured this segment; a stable key is added to it here as a side effect on first sight.
    private static bool IsDuplicatePendingApproval(ToolApprovalRequestContent approvalRequest,
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
            // Add returns false when the key was already present → a duplicate.
            return !seenKeys.Add(key);
        }

        // No stable identifier at all: dedup by reference identity so the same instance is not captured twice.
        return pendingApprovals.Contains(approvalRequest);
    }

    // The orchestration path. Compiles the package's OrchestrationSpec into the MAF-agnostic
    // OrchestrationAgentDefinition (bridging each participant's offer list with the SAME InvocationToolBridge switch the
    // single-agent path uses), drives the handoff workflow via IOrchestrationAgentFactory, and maps the normalized
    // OrchestrationUpdate stream onto the SAME transport/cap/sequence/approval plumbing as the single-agent loop. The
    // workflow itself owns multi-hop tool invocation; this loop only fans deltas out and round-trips approvals.
    private async Task RunOrchestrationAsync(RuntimePackage package,
        OrchestrationSpec spec,
        string resolvedModel,
        StreamTransport transport,
        StreamState stream,
        TurnPolicy turnPolicy,
        int? effectiveContextTokens,
        CancellationToken invocationToken)
    {
        var definition = await BuildOrchestrationDefinitionAsync(package, spec, resolvedModel, effectiveContextTokens, transport, invocationToken).ConfigureAwait(false);

        // Unify with the single-agent path (see TurnPolicy): the workflow seed is budgeted the same way the
        // single-agent path budgets its initial assembly, so a long conversation cannot silently overrun the window
        // any participant is launched with. Previously unbudgeted — the workflow ran on the raw seed regardless of
        // length.
        var budgetGate = new ContextBudgetNoticeGate();
        var seed = await ApplyContextBudgetAsync(BuildChatMessages(package), package, BuildToolBudgetDefinitions(package), resolvedModel, "orchestration-seed", turnPolicy, transport, budgetGate)
            .ConfigureAwait(false);

        await using var session = await _orchestrationAgentFactory.CreateAsync(definition, seed, invocationToken).ConfigureAwait(false);

        // Drain to the natural end of WatchAsync rather than breaking on the first TerminalOutput: the factory's
        // session drives the workflow as the stream is pulled and ends the stream right after the terminal output, so
        // a full drain is the documented terminator (an early break would risk truncating a later-superstep delta in
        // autonomous/multi-turn shapes). The terminal output carries no further deltas, so this adds no idle latency.
        string? activeParticipantKey = null;
        await foreach (var update in session.WatchAsync(invocationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.ParticipantKey)
                && !string.Equals(activeParticipantKey, update.ParticipantKey, StringComparison.Ordinal))
            {
                if (activeParticipantKey is not null)
                {
                    ProviderCallBudget.Current?.RecordAgentHandoff();
                }

                activeParticipantKey = update.ParticipantKey;
            }

            switch (update.Kind)
            {
                case OrchestrationUpdateKind.ReasoningDelta when !string.IsNullOrEmpty(update.Text):
                    await transport.EmitReasoningAsync(stream, update.Text, invocationToken).ConfigureAwait(false);
                    break;

                case OrchestrationUpdateKind.TextDelta when !string.IsNullOrEmpty(update.Text):
                    await transport.EmitTextAsync(stream, update.Text, invocationToken).ConfigureAwait(false);
                    break;

                case OrchestrationUpdateKind.ApprovalRequest when update.RequestId is { } requestId:
                    // Surface the approval over the existing transport (the same hub round-trip the single-agent path
                    // uses), then answer it on the HELD run and keep draining — the tool executes in a later superstep.
                    // Name the tool in the approval description so the card matches the single-agent UX (not the opaque id).
                    var pendingApproval = ToApprovalRequest(update);
                    var approvalDescription = $"Tool '{ApprovalToolName(update)}' requires approval before it runs.";
                    var approved = await RequestToolApprovalAsync(package, pendingApproval, invocationToken, approvalDescription).ConfigureAwait(false);
                    await session.RespondToApprovalAsync(requestId,
                        approved,
                        approved ? "Approved by user." : "Rejected by user.",
                        invocationToken).ConfigureAwait(false);
                    break;

                case OrchestrationUpdateKind.Failure:
                    // Map a workflow failure onto the existing agent-runtime failure path. The raw MAF executor detail
                    // is logged server-side only; the client gets a CONSTANT safe message (MapFailure does not redact a
                    // plain InvalidOperationException), so framework internals never leak to the caller.
                    _logger.LogWarning("Orchestration run failed for invocation {InvocationId}: {Detail}", package.InvocationId, update.Text);
                    throw new InvalidOperationException(OrchestrationFailureMessage);

                case OrchestrationUpdateKind.TerminalOutput:
                    // The workflow has produced its final output; no further deltas follow. Keep draining so the stream
                    // ends naturally (the factory's documented terminator) rather than breaking the enumeration early.
                    break;
            }
        }
    }

    public async Task RunAsync(RuntimePackage package, CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.Create(package, Guid.Empty, epochVersion: 0, ReadOnlyMemory<byte>.Empty);
        await RunAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExecuteApiToolCallAsync(Guid invocationId,
        string toolName,
        string parameters,
        bool requiresApproval,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(parameters);

        var requestId = Guid.NewGuid().ToString("N");
        var approvalCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultCompletion = new TaskCompletionSource<ToolCallResultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingToolCall = new PendingToolCall(invocationId, DateTimeOffset.UtcNow, approvalCompletion, resultCompletion);
        var sender = _hubSender.Value;
        var dispatcher = _eventDispatcher.Value;

        if (!_pendingToolCalls.TryAdd(requestId, pendingToolCall))
        {
            throw new InvalidOperationException("Failed to register pending tool call.");
        }

        // Tracks whether the Requested lifecycle phase was emitted so the timeout/cancel catch paths can emit a
        // matching Completed (IsError=true) exactly once. The React UI only clears a tool card on Completed, so a
        // timed-out tool without this would stay stuck in requesting/waiting forever.
        var requestedLifecycleEmitted = false;

        try
        {
            var payload = new ToolCallRequestPayload
            {
                InvocationId = invocationId,
                RequestId = requestId,
                ToolName = toolName,
                Parameters = parameters
            };

            // Approval gating: only tools that opt in (RequiresApproval) run the approval round-trip. All beta
            // tools ship as non-approval, so this branch is dormant today but keeps the wiring in place for a
            // future approval UI.
            if (requiresApproval)
            {
                var approvalPayload = new ApprovalRequestPayload
                {
                    InvocationId = invocationId,
                    RequestId = requestId,
                    Description = $"Tool '{toolName}' requested with parameters: {parameters}"
                };

                await sender.SendApprovalRequestAsync(approvalPayload, cancellationToken).ConfigureAwait(false);
                await dispatcher.ReportApprovalRequestedAsync(approvalPayload).ConfigureAwait(false);

                // Surface the pending approval on the LOCAL chat stream. This API-tool path emits its
                // tool-call-requested lifecycle only AFTER approval, so the browser has no card yet — the CallId is the
                // request id, and the reducer creates the waiting card from this event.
                await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
                {
                    InvocationId = invocationId,
                    RequestId = requestId,
                    CallId = requestId,
                    ToolName = toolName,
                    Description = approvalPayload.Description
                }).ConfigureAwait(false);

                using var approvalTimeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                approvalTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

                var approved = await approvalCompletion.Task.WaitAsync(approvalTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
                if (!approved)
                {
                    throw new WorkerToolCallException(toolName, "Tool call was rejected by the user.");
                }
            }

            await sender.SendToolCallRequestAsync(payload,
                cancellationToken).ConfigureAwait(false);
            await dispatcher.ReportToolCallRequestedAsync(payload).ConfigureAwait(false);
            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = invocationId,
                ToolCallId = requestId,
                ToolName = toolName,
                Phase = ToolCallLifecyclePhase.Requested,
                Arguments = parameters,
                RequiresApproval = requiresApproval
            }).ConfigureAwait(false);
            requestedLifecycleEmitted = true;

            // The tool-RESULT wait honours the active package's ToolCallTimeoutSeconds (falling back to the node-global
            // age when the call is not tied to an active invocation). The approval wait above intentionally keeps the
            // node-global age: it bounds a human decision, not tool execution, so it must not shrink to the short
            // per-tool budget.
            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellationTokenSource.CancelAfter(ResolveToolResultTimeout(invocationId));

            var result = await resultCompletion.Task.WaitAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
            var isError = !string.IsNullOrWhiteSpace(result.Error);

            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = invocationId,
                ToolCallId = requestId,
                ToolName = toolName,
                Phase = ToolCallLifecyclePhase.Completed,
                Result = isError ? result.Error : result.Result,
                IsError = isError
            }).ConfigureAwait(false);

            if (isError)
            {
                throw new WorkerToolCallException(toolName, result.Error!);
            }

            return result.Result;
        }
        catch (TimeoutException timeoutException)
        {
            await TryEmitTimeoutCompletedLifecycleAsync(dispatcher, requestedLifecycleEmitted, invocationId, requestId, toolName, timeoutException.Message).ConfigureAwait(false);
            throw new WorkerToolCallException(toolName, timeoutException.Message, timeoutException);
        }
        catch (OperationCanceledException operationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string TimeoutReason = "Tool call timed out waiting for a result.";
            await TryEmitTimeoutCompletedLifecycleAsync(dispatcher, requestedLifecycleEmitted, invocationId, requestId, toolName, TimeoutReason).ConfigureAwait(false);
            throw new WorkerToolCallException(toolName, TimeoutReason, operationCanceledException);
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    ///     Carries a framework-surfaced <see cref="ToolApprovalRequestContent" /> across the existing approval
    ///     transport and waits for the remote/local decision. Reuses the <see cref="_pendingToolCalls" /> approval
    ///     completion (resolved by <see cref="ResolveApprovalResult" />) and the pending-tool-call age as the wait
    ///     timeout. The result feeds the threadless resume in <see cref="RunAsync(InvocationExecutionContext, CancellationToken)" />.
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
    private async Task<bool> RequestToolApprovalAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        CancellationToken cancellationToken,
        string? descriptionOverride = null)
    {
        // Approval-decision audit: the tool name (drives both the category lookup and the audit row) and the
        // request→decision stopwatch are captured here so the resolved decision below can record a content-free audit row
        // and metric. Both are needed in the guards and in the timeout catch as well, so they live outside the try.
        var approvalToolName = (approvalRequest.ToolCall as FunctionCallContent)?.Name;
        var approvalRequestedTimestamp = Stopwatch.GetTimestamp();

        if (package.IsUnattended)
        {
            var reason = $"approval required in an unattended run: {approvalToolName ?? approvalRequest.ToolCall.CallId}";
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

            await sender.SendApprovalRequestAsync(approvalPayload, cancellationToken).ConfigureAwait(false);
            await dispatcher.ReportApprovalRequestedAsync(approvalPayload).ConfigureAwait(false);

            // Surface the pending approval on the LOCAL chat stream. The CallId is derived through the SAME
            // helper the streaming tool-call-requested lifecycle uses (CallId, falling back to the tool name) so both
            // events resolve the identical id — including for a non-null EMPTY-STRING CallId — and the browser can
            // attach the Approve/Deny controls to the matching tool-call card. In desktop/local mode there is no worker
            // hub to resolve the approval, so the loopback resolve endpoint feeds ResolveApprovalResult below. ToolCall
            // is the base ToolCallContent (CallId only); the concrete FunctionCallContent carries the tool name.
            var approvalCallId = ResolveToolCallCardId(approvalRequest.ToolCall.CallId, approvalToolName);
            await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
            {
                InvocationId = package.InvocationId,
                RequestId = requestId,
                CallId = approvalCallId,
                ToolName = string.IsNullOrEmpty(approvalToolName) ? approvalCallId : approvalToolName,
                Description = approvalPayload.Description
            }).ConfigureAwait(false);

            using var approvalTimeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            approvalTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

            bool approved;
            SetInvocationDeadline(parkedOnHuman: true);
            try
            {
                approved = await approvalCompletion.Task.WaitAsync(approvalTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
            }
            finally
            {
                SetInvocationDeadline(parkedOnHuman: false);
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
        if (toolName.StartsWith(CustomToolValidation.ToolNamePrefix, StringComparison.Ordinal))
        {
            if (package.CustomTools is not { Count: > 0 } customTools)
            {
                return null;
            }

            var customTool = customTools.FirstOrDefault(candidate => string.Equals(candidate.Name, toolName, StringComparison.Ordinal));
            if (customTool is null || !customTool.IsFixed)
            {
                return null;
            }

            return new ApprovalMemoKey(package.ConversationId, toolName, customTool.Name, customTool.Version, ResourceName: null);
        }

        if (package.Skills is not { Count: > 0 } skills)
        {
            return null;
        }

#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI; the same scoped suppression the provider call sites use.
        var isResourceRead = string.Equals(toolName, AgentSkillsProvider.ReadSkillResourceToolName, StringComparison.Ordinal);
        if (!isResourceRead && !string.Equals(toolName, AgentSkillsProvider.LoadSkillToolName, StringComparison.Ordinal))
        {
            return null;
        }
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

    /// <summary>
    ///     Whether a framework-surfaced approval request belongs to <c>ask_user</c>. Matched on the tool NAME rather
    ///     than on any flag, because the name is the only thing that survives the framework's approval wrapping —
    ///     <c>ToolApprovalRequestContent.ToolCall</c> is the base type and the concrete
    ///     <see cref="FunctionCallContent" /> is what carries it.
    /// </summary>
    private static bool IsUserQuestionRequest(ToolApprovalRequestContent approvalRequest) =>
        string.Equals((approvalRequest.ToolCall as FunctionCallContent)?.Name, AskUserTool.ToolName, StringComparison.Ordinal);

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
    private async Task<string> RequestUserAnswerAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        CancellationToken cancellationToken)
    {
        // The SAME id-derivation the streaming tool-call lifecycle uses, so the browser attaches the question card to
        // the tool-call card the model is waiting on — and so the handler's CurrentContext.CallContent.CallId lookup
        // finds what is stashed here.
        var callId = ResolveToolCallCardId(approvalRequest.ToolCall.CallId, AskUserTool.ToolName);

        // ResolveToolCallCardId deliberately preserves a non-null EMPTY-STRING CallId so the card key matches the
        // streaming lifecycle's. The stash cannot key on blank, so it falls back to the tool name — the handler will
        // then miss and return its fail-safe, which is the right degradation: a provider that emits no call id gives the
        // framework nothing to correlate on either, and a wrong answer is worse than an honest "not collected".
        var stashKey = string.IsNullOrEmpty(callId) ? AskUserTool.ToolName : callId;

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
            SetInvocationDeadline(parkedOnHuman: true);
            try
            {
                answers = await questionCompletion.Task.WaitAsync(questionTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
            }
            finally
            {
                SetInvocationDeadline(parkedOnHuman: false);
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

    /// <summary>
    ///     Re-points the whole-turn watchdog — the <c>CancelAfter</c> armed in <see cref="RegisterActiveInvocation" /> —
    ///     at a deadline measured from NOW, so a human round-trip is not charged to the model's turn budget.
    ///     <para>
    ///         Before parking on a human the deadline is pushed past the longest permitted wait; once the human has
    ///         answered it is re-armed to a full, fresh <c>InvocationTimeout</c>. This does NOT make any wait unbounded:
    ///         each wait keeps its own linked <c>CancelAfter(_maxPendingToolCallAge)</c>, which was previously dead code
    ///         because the shorter invocation deadline always fired first. The net effect is that
    ///         <c>MaxPendingToolCallAge</c> (operator-configurable, 10 min by default) becomes the real cap on operator
    ///         thinking time, instead of "whatever the model left over from its 300 s".
    ///     </para>
    ///     <para>
    ///         The park extension applies only while a client is ATTACHED. A park whose watcher has gone away is
    ///         waiting for an answer that cannot arrive, so it falls back to a plain <c>InvocationTimeout</c> backstop
    ///         and <c>DetachedInvocationReaper</c>'s grace normally ends it first. A run that never attached over the
    ///         hub at all — a scheduled run, a platform-hub run — is NOT detached and keeps today's full park budget.
    ///     </para>
    ///     <para>
    ///         Re-arming under <see cref="_syncRoot" /> is what makes it safe against a concurrent teardown:
    ///         <see cref="ClearActiveInvocation" /> nulls the field under the same lock BEFORE disposing the source, so a
    ///         non-null source observed here cannot already be disposed.
    ///     </para>
    /// </summary>
    private void SetInvocationDeadline(bool parkedOnHuman)
    {
        lock (_syncRoot)
        {
            _parkedOnHuman = parkedOnHuman;
            ApplyInvocationDeadline();
        }
    }

    // Caller must hold _syncRoot.
    private void ApplyInvocationDeadline()
    {
        if (_invocationCancellationTokenSource is not { } invocationCancellationTokenSource)
        {
            return;
        }

        // The parked deadline keeps the model's own budget on top of the human cap purely as a backstop: if the
        // re-arm on release were ever skipped, the turn still gets its normal InvocationTimeout rather than none.
        var extendPark = _parkedOnHuman
                         && _currentInvocationId is { } invocationId
                         && !_attachmentTracker.IsDetached(invocationId);
        invocationCancellationTokenSource.CancelAfter(extendPark ? _maxPendingToolCallAge + _invocationTimeout : _invocationTimeout);
    }

    // A client attaching or detaching mid-park changes which deadline the park is entitled to, and neither park site is
    // running code at that moment — so the re-arm has to come from here. Without it a reload during an approval park
    // would inherit whatever budget the detached park left behind.
    private void OnAttachmentChanged(object? sender, InvocationAttachmentChangedEventArgs args)
    {
        lock (_syncRoot)
        {
            if (_parkedOnHuman && _currentInvocationId == args.InvocationId)
            {
                ApplyInvocationDeadline();
            }
        }
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
        var source = IsLocalLoopbackInvocation(package) ? ApprovalDecisionSources.Local : ApprovalDecisionSources.Hub;
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

    // Derives the tool-call id that keys a tool-call card in the UI: the wire CallId when present, otherwise the tool
    // name (so an absent CallId still maps to a stable, human-meaningful key). Shared by the streaming tool-call
    // lifecycle and the approval lifecycle so both events resolve the SAME id for the same call — including a non-null
    // EMPTY-STRING CallId, which the two paths previously handled differently — letting the browser attach the
    // Approve/Deny controls to the matching card. Internal (not private) purely as a test seam via
    // InternalsVisibleTo; not part of the public contract.
    internal static string ResolveToolCallCardId(string? callId, string? toolName) =>
        callId ?? toolName ?? string.Empty;

    // Mirrors the normal Completed lifecycle emission for the timeout/cancel rethrow paths, emitting Completed with
    // IsError=true so a tool card the UI parked on Requested gets cleared instead of spinning forever. Skips when no
    // Requested was emitted (e.g. a timeout during the approval wait), so Completed never fires without a Requested.
    private static async Task TryEmitTimeoutCompletedLifecycleAsync(IWorkerEventDispatcher dispatcher,
        bool requestedLifecycleEmitted,
        Guid invocationId,
        string requestId,
        string toolName,
        string error)
    {
        if (!requestedLifecycleEmitted)
        {
            return;
        }

        await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
        {
            InvocationId = invocationId,
            ToolCallId = requestId,
            ToolName = toolName,
            Phase = ToolCallLifecyclePhase.Completed,
            Result = error,
            IsError = true
        }).ConfigureAwait(false);
    }

    private async Task TryReportCapabilitiesAfterInvocationAsync(Guid invocationId)
    {
        try
        {
            var reportTask = _capabilityReporter.ReportToApiAsync(CancellationToken.None);
            if (reportTask is not null)
            {
                await reportTask.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // Best-effort, post-invocation telemetry. In standalone desktop mode there is no remote worker hub to
            // report to (the connection is never active), so this fires benignly after every chat — log at Debug to
            // keep the operator console clean. Genuine worker-mode reporting issues surface elsewhere.
            _logger.LogDebug(exception, "Could not report capabilities after invocation {InvocationId} (no active worker hub in desktop mode).", invocationId);
        }
    }

    // Registers the invocation's active-completion source. Returns null when the node is draining and this is a local
    // turn — the completion add and the draining check happen under _syncRoot so they are serialized with
    // the drain snapshot, closing the admission-after-snapshot race. A non-local (remote) turn is not fenced here; the
    // dispatcher already stops accepting remote assignments at drain.
    private TaskCompletionSource? RegisterActiveInvocationCompletion(Guid invocationId, bool isLocalLoopback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_syncRoot)
        {
            if (_draining && isLocalLoopback)
            {
                return null;
            }

            if (!_activeInvocationCompletions.TryAdd(invocationId, completion))
            {
                throw new InvalidOperationException($"Invocation {invocationId} is already tracked as active.");
            }
        }

        return completion;
    }

    private void CompleteActiveInvocation(Guid invocationId, TaskCompletionSource completion)
    {
        _activeInvocationCompletions.TryRemove(invocationId, out _);
        completion.TrySetResult();
    }

    private async Task TrySendFailureAsync(IHubMessageSender sender,
        InvocationExecutionContext context,
        string error,
        FailureCategory failureCategory)
    {
        try
        {
            if (!context.IsEncrypted)
            {
                await sender.SendInvocationFailedAsync(new InvocationFailedPayload
                {
                    InvocationId = context.Package.InvocationId,
                    MessageId = context.MessageId == Guid.Empty ? null : context.MessageId,
                    Error = error,
                    FailureCategory = failureCategory.ToString()
                }, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await sender.SendEncryptedFailedAsync(new EncryptedFailedEnvelopeV1
                    {
                        ConversationId = context.Package.ConversationId,
                        MessageId = context.MessageId,
                        EpochVersion = context.EpochVersion,
                        Error = error,
                        FailureCategory = failureCategory.ToString()
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report invocation failure to the API for {InvocationId}. Enqueueing to dead letter store.", context.Package.InvocationId);
            await _deadLetterStore.EnqueueAsync(new InvocationFailedPayload
            {
                InvocationId = context.Package.InvocationId,
                MessageId = context.MessageId == Guid.Empty ? null : context.MessageId,
                Error = error,
                FailureCategory = failureCategory.ToString()
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private TimeSpan ResolveToolResultTimeout(Guid invocationId)
    {
        return _toolResultTimeoutsByInvocation.TryGetValue(invocationId, out var timeout)
            ? timeout
            : _maxPendingToolCallAge;
    }

    // Attributes the cancellation that ended the turn, from state that is already observable when the failure is
    // mapped: a deliberate cancel recorded by its own requester, the linked caller token, or — by elimination — the
    // invocation source's CancelAfter watchdog. Deliberately does NOT consult a flag set from a token callback:
    // callbacks run in reverse registration order, so a callback the runner registers at invocation registration is
    // invoked AFTER every later registration (the streaming agent's own), and the released agent can reach this
    // mapping before it ever ran — which reported a genuine watchdog timeout as a plain cancellation.
    // Resolved ONCE per cancelled turn so the failure category and the metric category cannot disagree.
    private CancellationOrigin ResolveCancellationOrigin()
    {
        lock (_syncRoot)
        {
            if (_requestedCancellationOrigin != CancellationOrigin.Unknown)
            {
                return _requestedCancellationOrigin;
            }

            if (_hostCancellationToken.IsCancellationRequested)
            {
                return CancellationOrigin.Shutdown;
            }

            // Nobody asked and the caller's token is still live, so a cancelled invocation source can only be its own
            // CancelAfter watchdog. An OperationCanceledException arriving with nothing cancelled is not attributable
            // to this node's timeout and stays a plain cancellation.
            return _invocationCancellationTokenSource?.IsCancellationRequested == true
                ? CancellationOrigin.Watchdog
                : CancellationOrigin.Shutdown;
        }
    }

    private static FailureCategory ClassifyCancellation(CancellationOrigin origin)
    {
        return origin == CancellationOrigin.Watchdog ? FailureCategory.Timeout : FailureCategory.Cancelled;
    }

    // The cancellation cause for the invocation_cancelled_total metric (AUD4-19): an explicit user cancel, the
    // invocation-level timeout firing ("watchdog"), or an external cancellation — the caller/host token or a
    // disconnect-driven CancelAll — reported as "shutdown".
    private static string ClassifyCancellationMetricCategory(CancellationOrigin origin)
    {
        return origin switch
        {
            CancellationOrigin.User => "user",
            CancellationOrigin.Watchdog => "watchdog",
            CancellationOrigin.DetachedGraceExpired => "detached_grace",
            _ => "shutdown"
        };
    }

    private void CancelPendingToolCalls(Guid invocationId)
    {
        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (pendingToolCall.Value.InvocationId != invocationId)
            {
                continue;
            }

            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                removedPendingToolCall.ApprovalCompletion.TrySetCanceled(CancellationToken.None);
                removedPendingToolCall.ResultCompletion.TrySetCanceled(CancellationToken.None);
            }
        }
    }

    private void RegisterActiveInvocation(Guid invocationId, TimeSpan invocationTimeout, CancellationToken cancellationToken)
    {
        CancellationTokenSource? invocationCancellationTokenSource = null;

        try
        {
            invocationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            invocationCancellationTokenSource.CancelAfter(invocationTimeout);

            lock (_syncRoot)
            {
                if (_currentInvocationId is not null)
                {
                    throw new InvalidOperationException("Worker is busy with another invocation");
                }

                _currentInvocationId = invocationId;
                _requestedCancellationOrigin = CancellationOrigin.Unknown;
                _hostCancellationToken = cancellationToken;
                _invocationCancellationTokenSource = invocationCancellationTokenSource;

                // Retained so a human round-trip can re-arm this same deadline (see SetInvocationDeadline).
                _invocationTimeout = invocationTimeout;
                invocationCancellationTokenSource = null;
            }
        }
        finally
        {
            invocationCancellationTokenSource?.Dispose();
        }
    }

    private CancellationToken GetInvocationCancellationToken()
    {
        lock (_syncRoot)
        {
            if (_invocationCancellationTokenSource is null)
            {
                throw new InvalidOperationException("No active invocation is registered.");
            }

            return _invocationCancellationTokenSource.Token;
        }
    }

    private bool IsCurrentInvocation(Guid invocationId)
    {
        lock (_syncRoot)
        {
            return _currentInvocationId == invocationId;
        }
    }

    private void ClearActiveInvocation(Guid invocationId)
    {
        CancellationTokenSource? invocationCancellationTokenSource;

        lock (_syncRoot)
        {
            if (_currentInvocationId != invocationId)
            {
                return;
            }

            invocationCancellationTokenSource = _invocationCancellationTokenSource;
            _invocationCancellationTokenSource = null;
            _invocationTimeout = TimeSpan.Zero;
            _parkedOnHuman = false;
            _currentInvocationId = null;
            _requestedCancellationOrigin = CancellationOrigin.Unknown;
            _hostCancellationToken = CancellationToken.None;
        }

        invocationCancellationTokenSource?.Dispose();
    }

    /// <summary>
    ///     What ended a cancelled invocation. <see cref="Unknown" /> is the resting value: no deliberate cancel was
    ///     requested, so the origin is derived from the caller token and the invocation source in
    ///     <see cref="ResolveCancellationOrigin" />.
    /// </summary>
    private enum CancellationOrigin
    {
        Unknown = 0,
        User = 1,
        Watchdog = 2,
        Shutdown = 3,

        /// <summary>
        ///     The disconnect grace elapsed with no client attached (<c>DetachedInvocationReaper</c>). Classified as a
        ///     plain cancellation like a user stop — the turn was abandoned, not timed out — but kept distinct so the
        ///     logs and the cancellation metric can tell an abandoned turn from one the operator stopped.
        /// </summary>
        DetachedGraceExpired = 4
    }
}
