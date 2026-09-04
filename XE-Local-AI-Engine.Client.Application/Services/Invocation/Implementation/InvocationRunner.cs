namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Represents invocation runner.
/// </summary>
public sealed partial class InvocationRunner : IInvocationRunner
{
    private const string OrchestrationFailureMessage = "Orchestration run failed.";

    // The effort a swapped turn falls back to when the fast model goes missing before its first token. It is the FAST
    // tier's own graded level, so the re-run keeps the tier the dispatcher chose and only gives up the model.
    private const string FallbackDispatchEffort = "low";

    /// <summary>The authored effort that opens the dispatch path, and the value persisted as <c>authored_effort</c>.</summary>
    private const string AutoReasoningEffort = "auto";

    // A new local turn admitted after shutdown drain has begun. Surfaced as a clean Cancelled-category
    // failure — the node is going away — rather than being run into a drain that has already stopped waiting for it.
    private const string NodeDrainingMessage = "The node is shutting down and is not accepting new requests.";

    // The budgeter's hard-stop (see ApplyContextBudgetAsync): history still exceeds the resolved context budget after
    // the two-pass truncation. A fixed, path-free constant carrying no token counts, model names, or content.
    private const string ContextBudgetExceededMessage =
        "Conversation exceeds the model's context window even after truncation — Compact the conversation to summarize older messages, start a new chat, or switch to a larger-context model.";

    private readonly ICapabilityReporter _capabilityReporter;

    // Read once per turn to pin the external binding this invocation is authorized against. See
    // ExternalProviderInvocationPin for what the pin protects and why it is seeded here.
    private readonly IExternalProviderRegistry _externalProviderRegistry;
    private readonly IConversationContextBudgeter _contextBudgeter;
    private readonly ConversationContextBudgetOptions _contextBudgetOptions;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly string _defaultModel;
    private readonly IEnvelopeCryptoService _envelopeCryptoService;
    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;
    private readonly Lazy<IHubMessageSender> _hubSender;
    private readonly ApiToolCallBridge _apiToolCallBridge;
    private readonly IInvocationAgentFactory _invocationAgentFactory;
    private readonly InvocationLifecycleTracker _lifecycleTracker;
    private readonly LocalRuntimeWarmer _localRuntimeWarmer;
    private readonly ILogger<InvocationRunner> _logger;
    private readonly TimeSpan _maxPendingToolCallAge;
    private readonly int _maxResponseSizeBytes;

    private readonly IOrchestrationAgentFactory _orchestrationAgentFactory;

    // The SAME dictionary instance ToolApprovalCoordinator and ApiToolCallBridge hold (see PendingToolCallRegistry):
    // the cancel/drain path below and the tool-result post must observe the calls those two registered.
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls;

    private readonly ProviderCallBudgetOptions _providerCallBudgetOptions;
    private readonly IProviderStreamResilience _providerStreamResilience;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly ProviderResilienceOptions _resilienceOptions;
    private readonly IRuntimePackageValidator _runtimePackageValidator;

    // A SINGLETON service, so this runner may hold it. The reasoning-effort dispatcher it opens a scope for is SCOPED
    // (two of its dependencies are), and a singleton may not capture that under any wrapper — Lazy<T> defers
    // construction but never opens a scope, which is exactly the captive dependency ValidateScopes exists to catch.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ToolApprovalCoordinator _toolApprovalCoordinator;

    private readonly SpawnOptions _spawnOptions;
    private readonly AgentToolPipelineOptions _toolPipelineOptions;
    private readonly IToolRelevanceCoreSet _toolRelevanceCoreSet;
    private readonly ToolRelevanceOptions _toolRelevanceOptions;

    public InvocationRunner(Lazy<IHubMessageSender> hubSender,
        Lazy<IWorkerEventDispatcher> eventDispatcher,
        IInvocationAgentFactory invocationAgentFactory,
        IOrchestrationAgentFactory orchestrationAgentFactory,
        IEnvelopeCryptoService envelopeCryptoService,
        IRuntimePackageValidator runtimePackageValidator,
        ICapabilityReporter capabilityReporter,
        ILocalModelProviderResolver providerResolver,
        LocalRuntimeWarmer localRuntimeWarmer,
        IDeadLetterStore deadLetterStore,
        IProviderStreamResilience providerStreamResilience,
        IConversationContextBudgeter contextBudgeter,
        IOptions<ConversationContextBudgetOptions> contextBudgetOptions,
        IOptions<ProviderResilienceOptions> resilienceOptions,
        IOptions<AgentToolPipelineOptions> toolPipelineOptions,
        IOptions<ProviderCallBudgetOptions> providerCallBudgetOptions,
        IOptions<ToolRelevanceOptions> toolRelevanceOptions,
        IToolRelevanceCoreSet toolRelevanceCoreSet,
        IConfiguration configuration,
        INodeRuntimeSettings runtimeSettings,
        IOptions<SpawnOptions> spawnOptions,
        PendingToolCallRegistry pendingToolCallRegistry,
        ToolApprovalCoordinator toolApprovalCoordinator,
        ApiToolCallBridge apiToolCallBridge,
        InvocationLifecycleTracker lifecycleTracker,
        IExternalProviderRegistry externalProviderRegistry,
        IServiceScopeFactory scopeFactory,
        ILogger<InvocationRunner> logger)
    {
        _hubSender = hubSender ?? throw new ArgumentNullException(nameof(hubSender));
        _lifecycleTracker = lifecycleTracker ?? throw new ArgumentNullException(nameof(lifecycleTracker));
        _toolApprovalCoordinator = toolApprovalCoordinator ?? throw new ArgumentNullException(nameof(toolApprovalCoordinator));
        _apiToolCallBridge = apiToolCallBridge ?? throw new ArgumentNullException(nameof(apiToolCallBridge));
        ArgumentNullException.ThrowIfNull(pendingToolCallRegistry);
        _pendingToolCalls = pendingToolCallRegistry.Calls;
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _invocationAgentFactory = invocationAgentFactory ?? throw new ArgumentNullException(nameof(invocationAgentFactory));
        _orchestrationAgentFactory = orchestrationAgentFactory ?? throw new ArgumentNullException(nameof(orchestrationAgentFactory));
        _envelopeCryptoService = envelopeCryptoService ?? throw new ArgumentNullException(nameof(envelopeCryptoService));
        _runtimePackageValidator = runtimePackageValidator ?? throw new ArgumentNullException(nameof(runtimePackageValidator));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _localRuntimeWarmer = localRuntimeWarmer ?? throw new ArgumentNullException(nameof(localRuntimeWarmer));
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
        ArgumentNullException.ThrowIfNull(toolRelevanceOptions);
        _toolRelevanceOptions = toolRelevanceOptions.Value;
        _toolRelevanceCoreSet = toolRelevanceCoreSet ?? throw new ArgumentNullException(nameof(toolRelevanceCoreSet));
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(spawnOptions);
        _spawnOptions = spawnOptions.Value;
        _externalProviderRegistry = externalProviderRegistry ?? throw new ArgumentNullException(nameof(externalProviderRegistry));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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
    }

    public int ActiveInvocationCount => _lifecycleTracker.ActiveInvocationCount;

    public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var package = context.Package;

        // Mark the turn's processing start (baseline for the pre-spawn latency + TTFT metrics) and open a
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

        _lifecycleTracker.RegisterActiveInvocation(package.InvocationId, turnPolicy.InvocationTimeout, cancellationToken);
        var activeInvocationCompletion = _lifecycleTracker.RegisterActiveInvocationCompletion(package.InvocationId, !shouldSendHubMessages);
        if (activeInvocationCompletion is null)
        {
            // Shutdown drain has started and this is a local turn admitted after the drain snapshot. Undo the
            // registration above and surface a clean, classified failure instead of running it into a drain that has
            // stopped waiting. A local turn sends no hub messages, so reporting to the dispatcher is the whole surface.
            _lifecycleTracker.ClearActiveInvocation(package.InvocationId);
            _logger.LogInformation("Rejecting local invocation {InvocationId}: the node is draining for shutdown.", package.InvocationId);
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, NodeDrainingMessage, FailureCategory.Cancelled).ConfigureAwait(false);
            return;
        }

        _apiToolCallBridge.SetToolResultTimeout(package.InvocationId, turnPolicy.ToolResultTimeout);

        using var providerBudgetScope = ProviderCallBudget.BeginScope(_providerCallBudgetOptions, harnessStartedTimestamp);
        var providerBudget = ProviderCallBudget.Current!;

        // Declared ahead of the terminal-telemetry local function below, which reads the readiness duration off it: a
        // local function may only capture a local that is already in scope where it is written.
        StreamState? stream = null;

        // The turn's tool-schema token estimate, onto the invocation state so the terminalize write persists it with
        // the envelope row. CaptureEfficiencySnapshot is a pure counter read, so calling it once per terminal path is
        // free; it is reported on the failed and cancelled paths too, because the number is most interesting on a turn
        // that ran out of context. Counts only — no tool name reaches this seam.
        // Telemetry never decides an outcome: the report runs immediately BEFORE each terminal report, so on the
        // completed path a throw here would fall into the catch below and turn a finished turn into a failed one, and
        // on the two failure paths it would replace the real classification with its own. The shipped dispatcher
        // cannot throw (UpdateInvocation is a logged no-op for an unknown id), which is exactly why swallowing costs
        // nothing and why the guard is worth having against a future one that can.
        // The model-readiness duration rides the SAME helper for the same three-path reason: the cold start it measures
        // sits inside the whole-turn clock, so a turn that failed after a 200 s model load must still be able to say so.
        // Null on every turn with no local warm (Ollama, a remote provider), which is what the column means.
        async Task ReportTerminalTelemetryAsync()
        {
            try
            {
                var efficiency = providerBudget.CaptureEfficiencySnapshot();
                await dispatcher.ReportToolSchemaTokensAsync(package.InvocationId, efficiency.ToolSchemaTokens, efficiency.MaximumToolSchemaTokens).ConfigureAwait(false);
                await dispatcher.ReportModelReadinessAsync(package.InvocationId,
                                   stream?.ModelReadinessDurationMs is { } readinessMs ? (long)readinessMs : null)
                               .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not report the terminal telemetry for invocation {InvocationId}; the turn's outcome is unaffected.", package.InvocationId);
            }
        }

        // Seeded in the SAME place and for the same reason as the provider budget: the send-time relevance hop runs
        // several awaited frames below this method, an AsyncLocal write in a callee never reaches its caller, and the
        // scope has to exist before the agent is built. Inactive by default, in which case the hop is a
        // reference-equality pass-through and list_tools is never appended.
        // The core set is read only when it will be USED: GetCoreToolNames reads the MCP registry live and, with any
        // server connected, allocates a fresh catalog list plus a set per turn. On the shipped default that work would
        // build a set nothing reads, on the hot path of every chat turn.
        var toolRelevanceActive = _toolRelevanceOptions.Enabled && !package.DisableToolRelevanceFilter;
        using var toolRelevanceScope = ToolRelevanceScope.BeginScope(toolRelevanceActive,
            toolRelevanceActive ? _toolRelevanceCoreSet.GetCoreToolNames() : FrozenSet<string>.Empty);
        string? invocationOutcome = null;

        try
        {
            var invocationToken = _lifecycleTracker.GetInvocationCancellationToken();
            ModelResolution modelResolution;
            using (NodeActivitySource.Source.StartActivity("chat.invocation.resolve_model"))
            {
                modelResolution = await ResolveModelAsync(package.ModelProfile, invocationToken).ConfigureAwait(false);
            }

            var resolvedModel = modelResolution.Model;

            // Reasoning effort `auto`: resolve it into a concrete {model, effort} for THIS turn, here,
            // between model resolution and the local warm — so an admitted small-model swap is warmed instead of the
            // resolved model, rather than being reacted to after a warm that silently swallows its failure.
            //
            // The single Normalize(...) is "auto" guard below is the whole byte-identical story for every other
            // effort: no scope, no dispatcher resolution, no request allocation, no node-side lookup. The package
            // builder has already normalised, so the comparison is exact.
            //
            // The scope is declared FIRST so `using`'s reverse-order disposal releases it LAST — after the ledger
            // reservation produced by the CapacityService that lives inside it. A plain nullable IServiceScope (not
            // AsyncServiceScope, whose `default` cannot be disposed safely) keeps the non-`auto` path a legal no-op.
            using var dispatchScope = ReasoningEffortNormalizer.Normalize(package.ReasoningEffort) is AutoReasoningEffort
                ? _scopeFactory.CreateScope()
                : null;

            ReasoningDispatchDecision? dispatchDecision = null;
            if (dispatchScope is not null)
            {
                dispatchDecision = await dispatchScope.ServiceProvider
                                                      .GetRequiredService<IReasoningEffortDispatcher>()
                                                      .DispatchAsync(BuildDispatchRequest(package, resolvedModel), invocationToken)
                                                      .ConfigureAwait(false);
            }

            // The model the turn was AUTHORISED for, and its capability flags, captured before the dispatch block can
            // rewrite any of them. The send-boundary retry below restores exactly these.
            var originalModel = resolvedModel;
            var originalSupportsThinking = package.SupportsThinking;
            var originalReasoningBudgetEnforceable = package.ReasoningBudgetEnforceable;
            using var fastReservation = dispatchDecision?.CapacityReservation;
            if (dispatchDecision is { } dispatched)
            {
                resolvedModel = dispatched.Model;

                // `package with { ... }` copies ConfigHash verbatim: the hash folds the AUTHORED effort, so two turns
                // of one conversation that dispatch to different tiers still share one hash and a resume is never
                // invalidated by a dispatch difference. Sampling is untouched: no tier caps the turn's output (see
                // ReasoningDispatchDecision.MaxOutputTokens), so a dispatched turn's send is shaped exactly like a
                // non-`auto` one.
                package = package with
                {
                    ReasoningEffort = dispatched.Effort,
                    SupportsThinking = dispatched.SupportsThinking,
                    ReasoningBudgetEnforceable = dispatched.ReasoningBudgetEnforceable
                };

                // Onto the invocation state, so the terminalize write persists what `auto` resolved to with the
                // envelope row. Two category labels: the tier, and the authored effort — which is `auto` by the
                // branch condition above, and is what separates the dispatched population from the pre-`auto` one in
                // the same query. Only an `auto` turn reaches this line, so every other turn's envelope carries nulls.
                await dispatcher.ReportEffortDispatchAsync(package.InvocationId, ReasoningTierLabels.For(dispatched.Tier), AutoReasoningEffort).ConfigureAwait(false);
            }

            var modelWasSwapped = dispatchDecision is { } swapCandidate
                                  && !string.Equals(swapCandidate.Model, originalModel, StringComparison.Ordinal);

            // The one server-side record of what `auto` decided. The dispatcher itself takes no logger by design (its
            // inputs are the user's message and the turn's shape), so the decision is logged here, from its OUTPUT
            // only: the tier, the stable kebab-case reason code, and whether the model was replaced. No signal value —
            // no message length, no conversation depth, no score — and no model name or message text ever reaches this
            // line, which is what keeps it inside the slice's logging invariant.
            if (dispatchDecision is { } logged)
            {
                _logger.LogInformation("Reasoning effort 'auto' dispatched for invocation {InvocationId}: tier {Tier}, reason {ReasonCode}, model swapped {ModelWasSwapped}.",
                    package.InvocationId, ReasoningTierLabels.For(logged.Tier), logged.ReasonCode, modelWasSwapped);
            }

            // The retry below re-enters RunSingleAgentAsync, which owns the tool-relevance drain and its ToolsFiltered
            // notice — running it twice would emit that notice twice. A swap requires OfferedToolCount == 0, so a
            // swapped turn offers no tools and the drain is a no-op; this makes that dependency explicit instead of
            // load-bearing-by-coincidence. If a future gate ever admits a swap on a tool-bearing turn, the retry
            // switches itself off rather than double-emitting.
            var swapRetryEligible = modelWasSwapped && package.AllowedTools.Count == 0;

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

            // Surface what `auto` resolved to, for the same reason: a decision the user did not make must be visible.
            // Deliberately silent on a NORMAL, no-swap turn — that is the common case and a notice on every ordinary
            // turn is noise. The detail is the reason CODE only; no signal value ever reaches this seam.
            // A SWAPPED turn is deliberately silent here: it may still fall back to the original model at the send
            // boundary below, and a turn must carry exactly ONE effort notice. Its notice is emitted once the send has
            // resolved — either naming the model that actually served, or naming the fallback.
            if (dispatchDecision is { } announced && announced.Tier != ReasoningTier.Normal && !modelWasSwapped)
            {
                await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                   BuildEffortDispatchedNoticeMessage(announced.Tier, announced.Effort, resolvedModel, swapped: false),
                                   announced.ReasonCode)
                               .ConfigureAwait(false);
            }

            // Seed the per-root-invocation spawn context (Depth 0) for this turn so the spawn_subagent tool (when the
            // agent calls it) enforces the fan-out and cloud-spawn caps against ONE shared root. The context flows as an
            // AsyncLocal into the function-invocation pipeline that runs the tool body; disposal restores the prior
            // ambient value. A turn that never spawns pays only a struct allocation.
            using var spawnRoot = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns, resolvedModel);

            // Pin the external binding this turn is authorized against, in the SAME scope as the spawn context and for
            // the same reason: the decision is made once, up front, and the provider re-reads configuration on every
            // send. Without the pin an operator edit landing between two rounds of a tool loop silently redirects the
            // later sends. A node-local or cloud model resolves nothing and the scope is inert.
            // The scope is opened HERE rather than inside the resolver: the ambient set is an AsyncLocal, and a write
            // to one inside an async method never reaches that method's caller.
            // BOTH models, not just the dispatched one: the send-boundary retry below switches `resolvedModel` back to
            // the original inside THIS scope, and a pin it never resolved leaves that fallback send falling through to
            // the transport's weaker unpinned check — the Local->Cloud / endpoint edit this pin exists to refuse. On a
            // non-`auto` turn, and on an `auto` turn that did not swap, the two are the same id and the resolver
            // de-duplicates, so the pin set is exactly what it was before.
            var turnPins = await ExternalProviderInvocationPin
                                 .ResolveAsync(_externalProviderRegistry, [resolvedModel, originalModel], invocationToken)
                                 .ConfigureAwait(false);
            using var externalBindingPin = ExternalProviderBindingPinScope.Begin(turnPins);

            // Seed the active conversation id into the same root tool-loop scope so the AgentHome tool gateway can stage
            // this conversation's uploaded attachments into the sandbox. Like the spawn context it flows as an
            // AsyncLocal through the function-invocation pipeline; disposal restores the prior ambient value.
            using var conversationScope = AgentRunConversationContext.BeginScope(package.ConversationId);

            // Warm the local model to readiness BEFORE the watched streaming pull begins, so a cold big-model
            // load happens in its OWN size-aware window (owned by the supervisor) and is never killed by the shorter
            // stream-idle watchdog — the primary cause of the audited "big model can never load through chat" hang.
            // Cloud (Codex/Azure) and Ollama models are a no-op here. The load is decoupled from this caller's token in
            // the supervisor, so a user who cancels merely abandons the wait while the load continues in the background.
            var requestedContextTokens = turnPolicy.RequestedContextTokens ?? turnPolicy.ContextCapacityTokens;
            var localRuntime = await _localRuntimeWarmer.PrepareLocalRuntimeAsync(resolvedModel, dispatcher, package.InvocationId, stream, turnStartedTimestamp, invocationToken).ConfigureAwait(false);
            var effectiveContextTokens = localRuntime.EffectiveContextTokens;

            // Fold the launched effective context window into the turn policy so the OUTER conversation
            // budgeter sizes history against the real window rather than the configured default (see
            // TurnPolicy.WithEffectiveContext for the precedence). The same value is threaded into the agent definition
            // below so the INNER provider-round budgeter (num_ctx side channel) resolves the identical window.
            // Captured BEFORE the fold so the send-boundary retry can re-derive the ORIGINAL model's policy from the
            // ORIGINAL model's own warm. Reusing the swapped model's policy would measure a 20k conversation against a
            // 4k fast-model window and drop history the authorised model would have kept.
            var preWarmPolicy = turnPolicy;
            turnPolicy = turnPolicy.WithEffectiveContext(effectiveContextTokens);

            if (context.GenerationAdmissionPolicy is { } admissionPolicy)
            {
                // The normal chat path deliberately lets generation retry a failed warm so the provider boundary can
                // surface its authoritative error. An admission-gated caller cannot do that: null effective context
                // would reject first and mask the captured provider failure. Preserve and rethrow that original failure
                // before consulting the policy; callers without a policy retain the existing retry-on-send behavior.
                localRuntime.WarmFailure?.Throw();

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
                    throw new InvocationGenerationRejectedException(LocalRuntimeWarmer.BuildGenerationAdmissionRejectionMessage(decision.RejectionReasonCode,
                        admissionContext));
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
                try
                {
                    await RunSingleAgentAsync(package, resolvedModel, transport, stream, turnPolicy, effectiveContextTokens, invocationToken).ConfigureAwait(false);

                    // A swap served the turn. Announce it now, once, naming the model that actually ran. Every other
                    // turn already emitted its notice (or is a silent NORMAL one) before the send.
                    if (modelWasSwapped && dispatchDecision is { } served)
                    {
                        // The invocation state was seeded with the model the PACKAGE named, and both the persisted
                        // message row and the envelope's provider attribution read it from there — so a swapped turn
                        // that does not correct it is recorded, and measured, against a model that never saw it.
                        await dispatcher.ReportServedModelAsync(package.InvocationId, resolvedModel).ConfigureAwait(false);
                        await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                           BuildEffortDispatchedNoticeMessage(served.Tier, served.Effort, resolvedModel, swapped: true),
                                           served.ReasonCode)
                                       .ConfigureAwait(false);
                    }
                }
                catch (Exception) when (swapRetryEligible && !stream.FirstOutputRecorded && !invocationToken.IsCancellationRequested)
                {
                    // The fast model went away between the capacity probe and the send: profiling took its process, an
                    // eject drained it, it was uninstalled, or it would not fit. Nothing has reached the client yet, so
                    // re-run once on the model the turn was actually authorised for. Keyed on "nothing streamed"
                    // rather than an exception type on purpose — one condition covers every way a swapped model can go
                    // missing and cannot rot when a provider's messages change.
                    //
                    // RELEASE THE FAST RESERVATION FIRST. It books the small model's bytes and, on an Allow verdict,
                    // holds a launch admission and one of the loaded-process slots. Carrying it into the re-run
                    // double-books the ledger against a model that is no longer being loaded and can starve the
                    // original model's own self-heal spawn on a node at the process cap — the exact failure this retry
                    // exists to avoid. Dispose is idempotent, so the `using` at turn end is a no-op after this.
                    fastReservation?.Dispose();
                    resolvedModel = originalModel;
                    package = package with
                    {
                        ReasoningEffort = FallbackDispatchEffort,
                        SupportsThinking = originalSupportsThinking,
                        ReasoningBudgetEnforceable = originalReasoningBudgetEnforceable
                    };

                    // Re-warm the ORIGINAL model and re-derive its window. The policy and effective-context above were
                    // both measured against the fast model's launched window; carrying them into the re-run would size
                    // this turn's history — and the agent definition's num_ctx — against a window this model never had.
                    var retryRuntime = await _localRuntimeWarmer.PrepareLocalRuntimeAsync(resolvedModel, dispatcher, package.InvocationId, stream, turnStartedTimestamp, invocationToken).ConfigureAwait(false);
                    var retryContextTokens = retryRuntime.EffectiveContextTokens;
                    var retryPolicy = preWarmPolicy.WithEffectiveContext(retryContextTokens);

                    await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                       BuildEffortDispatchedNoticeMessage(ReasoningTier.Fast, FallbackDispatchEffort, resolvedModel, swapped: false),
                                       ReasoningDispatchReasons.FastModelUnavailable)
                                   .ConfigureAwait(false);

                    // Exactly once. A second failure is a real failure and fails the turn normally.
                    await RunSingleAgentAsync(package, resolvedModel, transport, stream, retryPolicy, retryContextTokens, invocationToken).ConfigureAwait(false);
                }
                catch (Exception) when (modelWasSwapped && dispatchDecision is { } failedSwap)
                {
                    // The swapped send failed with no fallback available — it had already streamed, or the turn offers
                    // tools, or the turn is being cancelled. The ruling is exactly ONE effort notice per turn and the
                    // pre-send announcement was deliberately withheld for swapped turns, so without this the reader is
                    // told nothing at all about a turn whose model was silently replaced. The notice names the model
                    // that actually served; the FAILURE is reported by the outer handler, as for any other turn.
                    //
                    // No served-model report here: the turn produced no answer to attribute, and the fast model may
                    // have died before its first token. The seeded (authorised) model stays on the failed row.
                    await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                       BuildEffortDispatchedNoticeMessage(failedSwap.Tier, failedSwap.Effort, resolvedModel, swapped: true),
                                       failedSwap.ReasonCode)
                                   .ConfigureAwait(false);
                    throw;
                }
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

            await ReportTerminalTelemetryAsync().ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(package.InvocationId,
                stream.UsageSnapshot?.InputTokens,
                stream.UsageSnapshot?.OutputTokens,
                stream.UsageSnapshot?.TotalTokens,
                stream.UsageSnapshot?.ReasoningTokens,
                generationDurationMs,
                stream.FinishReason,
                stream.ToThroughput()).ConfigureAwait(false);
            invocationOutcome = "completed";
        }
        catch (OperationCanceledException) when (_lifecycleTracker.IsCurrentInvocation(package.InvocationId))
        {
            invocationOutcome = "cancelled";
            _lifecycleTracker.CancelPendingToolCalls(package.InvocationId);
            var cancellationOrigin = _lifecycleTracker.ResolveCancellationOrigin();
            var failureCategory = InvocationLifecycleTracker.ClassifyCancellation(cancellationOrigin);
            // The breadcrumb: one fixed, path-free sentence per cancellation cause. Every cause used to share the single
            // string "Invocation timed out or was cancelled", so a turn that ended at the node's message-request ceiling,
            // one the operator stopped, and one the detached-run reaper collected were indistinguishable in the persisted
            // failure — which is exactly why a live turn reported "Cancelled" at ~550s could not be attributed to anything.
            var cancellationMessage = InvocationLifecycleTracker.DescribeCancellation(cancellationOrigin, turnPolicy.InvocationTimeout);
            // Count the cancellation by its cause (user | watchdog | shutdown). Distinct from InvocationFailedTotal:
            // a cancel is an outcome, not a failure. An invocation-level timeout ("watchdog") is additionally surfaced as a
            // Timeout failure below via ReportInvocationFailedAsync — the two metrics answer different questions.
            NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", InvocationLifecycleTracker.ClassifyCancellationMetricCategory(cancellationOrigin)));
            await ReportTerminalTelemetryAsync().ConfigureAwait(false);
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, cancellationMessage, failureCategory).ConfigureAwait(false);
            if (shouldSendHubMessages)
            {
                await TrySendFailureAsync(sender, context, cancellationMessage, failureCategory).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            invocationOutcome = exception is LlamaServerModelEjectedException ? "cancelled" : "failed";
            _logger.LogError(exception, "Invocation {InvocationId} failed.", package.InvocationId);
            var (failureCategory, message) = InvocationFailureClassifier.MapFailure(exception);
            // An operator force-eject surfaces as a Cancelled-category LlamaServerModelEjectedException here (not the OCE
            // path). Count it as a cancellation cause rather than a failure, mirroring the OCE branch above.
            if (exception is LlamaServerModelEjectedException)
            {
                NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", "operator_eject"));
            }

            await ReportTerminalTelemetryAsync().ConfigureAwait(false);
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

            _apiToolCallBridge.CleanupStaleToolCalls(_maxPendingToolCallAge);
            _apiToolCallBridge.ClearToolResultTimeout(package.InvocationId);
            _lifecycleTracker.ClearActiveInvocation(package.InvocationId);
            _lifecycleTracker.CompleteActiveInvocation(package.InvocationId, activeInvocationCompletion);
            await TryReportCapabilitiesAfterInvocationAsync(package.InvocationId).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return _lifecycleTracker.DrainActiveInvocationsAsync(timeout, cancellationToken);
    }

    /// <inheritdoc />
    public void Cancel(Guid invocationId)
    {
        _lifecycleTracker.Cancel(invocationId);
    }

    /// <inheritdoc />
    public void CancelDetached(Guid invocationId)
    {
        _lifecycleTracker.CancelDetached(invocationId);
    }

    /// <inheritdoc />
    public void CancelAll()
    {
        _lifecycleTracker.CancelAll();
    }

    /// <inheritdoc />
    public void CleanupStaleToolCalls(TimeSpan maxAge)
    {
        _apiToolCallBridge.CleanupStaleToolCalls(maxAge);
    }

    /// <inheritdoc />
    public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
    {
        _toolApprovalCoordinator.ResolveApprovalResult(evt, scope);
    }

    /// <inheritdoc />
    public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt)
    {
        _toolApprovalCoordinator.ResolveUserQuestionResult(evt);
    }

    /// <inheritdoc />
    public Task<string> ExecuteApiToolCallAsync(Guid invocationId,
        string toolName,
        string parameters,
        CancellationToken cancellationToken = default)
    {
        return _apiToolCallBridge.ExecuteApiToolCallAsync(invocationId, toolName, parameters, cancellationToken);
    }

    public void ResolveToolCallResult(ToolCallResultEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_pendingToolCalls.TryRemove(evt.RequestId, out var pendingToolCall))
        {
            pendingToolCall.ResultCompletion.TrySetResult(evt);
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

    // True for a turn the node dispatched to ITSELF (local chat, scheduler, benchmarks): it carries the loopback
    // requested-capability marker, has no hub connection behind it, and therefore sends no hub messages and audits its
    // approval decisions as locally sourced.
    internal static bool IsLocalLoopbackInvocation(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return package.RequestedCapabilities?.Any(static capability => string.Equals(capability, LocalChatLoopbackDefaults.RequestedCapability, StringComparison.Ordinal)) == true;
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
        // Coarse span over the MAF agent build — another pre-first-token stage. Disposed right after the
        // build so it does not enclose the streaming loop; the agent context keeps its normal await-using scope.
        var buildAgentActivity = NodeActivitySource.Source.StartActivity("chat.invocation.build_agent");
        await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken).ConfigureAwait(false);
        buildAgentActivity?.Dispose();

        // Maps callId → the tool name plus what its Requested event already carried, so FunctionResultContent (which has
        // no Name) can resolve the tool name from the earlier FunctionCallContent with the matching CallId, and so a
        // re-emitted FunctionCallContent can be recognised as a repeat before it pays another serialize + dispatch.
        var pendingLocalToolCalls = new Dictionary<string, RequestedToolCall>(StringComparer.Ordinal);

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

                // Last-wins across the whole turn, segments included: an intermediate tool-call segment must not be the
                // reason the turn is recorded as having stopped.
                if (update.FinishReason is { } finishReason && !string.IsNullOrEmpty(finishReason.Value))
                {
                    stream.FinishReason = finishReason.Value;
                }

                // Folded on ARRIVAL, not once per drained stream. A tool-calling turn is several llama-server requests
                // inside ONE RunStreamingAsync — FunctionInvokingChatClient runs that loop internally, so the outer
                // do/while below only re-iterates for approval round-trips and never sees them. Keeping the last
                // reading therefore threw away every request but the final one (measured live: a turn reporting
                // prompt 283 + cached 2346 + generated 1720 against a usage total of 4349 — two requests, one recorded).
                // llama-server puts `timings` on the LAST chunk of each request and `timings_per_token` (which would
                // repeat it on intermediate chunks, double-counting here) is off by default and never set by us.
                stream.AddSegmentTimings(LlamaServerGenerationTimings.TryRead(update.RawRepresentation));

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
                                    pendingLocalToolCalls[callId] = new RequestedToolCall(alreadyRequested.Name, functionCall.Arguments, alreadyRequested.SerializedArguments);
                                    break;
                                }

                                pendingLocalToolCalls[callId] = new RequestedToolCall(functionCall.Name, functionCall.Arguments, serializedArguments);

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
                                if (!ToolApprovalCoordinator.IsDuplicatePendingApproval(approvalRequest, pendingApprovals, pendingApprovalKeys))
                                {
                                    pendingApprovals.Add(approvalRequest);
                                }

                                break;
                        }
                    }
                }

                if (usage is not null)
                {
                    // ACCUMULATED, not assigned — same reason as AddSegmentTimings above: one UsageContent arrives per
                    // provider round inside the single RunStreamingAsync, so last-wins reported only the final round.
                    stream.AddUsage(usage);
                    var cumulativeUsage = stream.UsageSnapshot!;
                    _logger.LogDebug("Received cumulative usage for invocation {InvocationId}: input={InputTokens}, output={OutputTokens}, reasoning={ReasoningTokens}, total={TotalTokens}.",
                        package.InvocationId,
                        cumulativeUsage.InputTokens,
                        cumulativeUsage.OutputTokens,
                        cumulativeUsage.ReasoningTokens,
                        cumulativeUsage.TotalTokens);
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

            // The tool-relevance notice, drained at the end of the FIRST segment so it FOLLOWS the first assistant
            // text exactly as HistoryTruncated does. The hop cannot reach the transport, so it leaves the two counts on
            // the ambient scope and the runner emits them here. Counts only: the notice never names a tool. A first
            // segment that THROWS terminalises without ever emitting it, which is deliberate — a "tools were held back"
            // line printed under an error reads as the cause when it is not, and the numbers survive in telemetry.
            if (isFirstSegment && ToolRelevanceScope.Current is { } relevanceState)
            {
                var hiddenToolCount = Volatile.Read(ref relevanceState.PendingNoticeHiddenCount);
                if (hiddenToolCount > 0)
                {
                    await transport.EmitNoticeAsync(TurnNoticeKind.ToolsFiltered,
                                       BuildToolsFilteredNoticeMessage(hiddenToolCount, Volatile.Read(ref relevanceState.PendingNoticeTotalCount)))
                                   .ConfigureAwait(false);
                }
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
                    if (ToolApprovalCoordinator.IsUserQuestionRequest(approvalRequest))
                    {
                        var answerNote = await _toolApprovalCoordinator.RequestUserAnswerAsync(package, approvalRequest, _lifecycleTracker.SetInvocationDeadline, invocationToken)
                                                                       .ConfigureAwait(false);
                        approvalResponses.Add(approvalRequest.CreateResponse(approved: true, answerNote));
                        continue;
                    }

                    var approved = await _toolApprovalCoordinator.RequestToolApprovalAsync(package, approvalRequest, _lifecycleTracker.SetInvocationDeadline, invocationToken).ConfigureAwait(false);
                    approvalResponses.Add(approvalRequest.CreateResponse(approved, approved ? "Approved by user." : "Rejected by user."));
                }

                currentMessages.Add(new ChatMessage(ChatRole.User, approvalResponses));
            }
        } while (pendingApprovals.Count > 0);
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

        // A participant runs on its OWN model, which the turn-level pin (seeded for the resolved turn model) does not
        // cover — so an external participant's sends would fall through to the transport's weaker unpinned check while
        // the workflow carries node-local tool results between participants. Pin every participant model up front, in
        // one scope: the workflow interleaves its participants inside this single async flow, so they cannot each own a
        // nested scope, and the pins are looked up by model id anyway.
        var resolvedParticipantPins = await ExternalProviderInvocationPin
                                            .ResolveAsync(_externalProviderRegistry,
                                                definition.Participants.Select(participant => participant.ModelId),
                                                invocationToken)
                                            .ConfigureAwait(false);
        using var participantPins = ExternalProviderBindingPinScope.Begin(resolvedParticipantPins);

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
                    var approved = await _toolApprovalCoordinator.RequestToolApprovalAsync(package, pendingApproval, _lifecycleTracker.SetInvocationDeadline, invocationToken, approvalDescription)
                                                                 .ConfigureAwait(false);
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

    // Derives the tool-call id that keys a tool-call card in the UI: the wire CallId when present, otherwise the tool
    // name (so an absent CallId still maps to a stable, human-meaningful key). Shared by the streaming tool-call
    // lifecycle and the approval lifecycle so both events resolve the SAME id for the same call — including a non-null
    // EMPTY-STRING CallId, which the two paths previously handled differently — letting the browser attach the
    // Approve/Deny controls to the matching card. Internal (not private) purely as a test seam via
    // InternalsVisibleTo; not part of the public contract.
    internal static string ResolveToolCallCardId(string? callId, string? toolName) =>
        callId ?? toolName ?? string.Empty;

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


    // A local tool call seen requested on the stream but not yet resulted: the tool name plus the arguments as they
    // arrived, kept so a repeated call for the same id can be detected and the result can be attributed to its tool.
    // Distinct from PendingToolCall, which tracks a WORKER tool call's approval/result completions.
    private readonly record struct RequestedToolCall(string Name, object? Arguments, string? SerializedArguments);
}
