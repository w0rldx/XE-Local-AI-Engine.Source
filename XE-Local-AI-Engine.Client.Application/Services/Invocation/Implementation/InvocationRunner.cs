namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
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
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
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
    private readonly string _defaultModel;
    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;
    private readonly ApiToolCallBridge _apiToolCallBridge;
    private readonly IInvocationAgentFactory _invocationAgentFactory;
    private readonly InvocationLifecycleTracker _lifecycleTracker;
    private readonly LocalRuntimeWarmer _localRuntimeWarmer;
    private readonly ILogger<InvocationRunner> _logger;
    private readonly TimeSpan _maxPendingToolCallAge;
    private readonly int _maxResponseSizeBytes;

    private readonly IOrchestrationAgentFactory _orchestrationAgentFactory;

    private readonly ProviderCallBudgetOptions _providerCallBudgetOptions;
    private readonly IProviderStreamResilience _providerStreamResilience;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly ProviderResilienceOptions _resilienceOptions;
    private readonly IRuntimePackageValidator _runtimePackageValidator;

    // Singleton, so this runner may hold it. The reasoning-effort dispatcher it opens a scope for is SCOPED (two of its
    // dependencies are), and capturing that under any wrapper — Lazy<T> defers construction but opens no scope — is the captive dependency ValidateScopes catches.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ToolApprovalCoordinator _toolApprovalCoordinator;

    private readonly SpawnOptions _spawnOptions;
    private readonly AgentToolPipelineOptions _toolPipelineOptions;
    private readonly IToolRelevanceCoreSet _toolRelevanceCoreSet;

    // STORED, never read at construction: the tool-relevance switch is read live per turn so an operator save applies
    // to the next turn without a restart (INodeRuntimeSettings' own doc forbids capturing a migrated value in a field).
    private readonly INodeRuntimeSettings _runtimeSettings;

    public InvocationRunner(Lazy<IWorkerEventDispatcher> eventDispatcher,
        IInvocationAgentFactory invocationAgentFactory,
        IOrchestrationAgentFactory orchestrationAgentFactory,
        IRuntimePackageValidator runtimePackageValidator,
        ICapabilityReporter capabilityReporter,
        ILocalModelProviderResolver providerResolver,
        LocalRuntimeWarmer localRuntimeWarmer,
        IProviderStreamResilience providerStreamResilience,
        IConversationContextBudgeter contextBudgeter,
        IOptions<ConversationContextBudgetOptions> contextBudgetOptions,
        IOptions<ProviderResilienceOptions> resilienceOptions,
        IOptions<AgentToolPipelineOptions> toolPipelineOptions,
        IOptions<ProviderCallBudgetOptions> providerCallBudgetOptions,
        IToolRelevanceCoreSet toolRelevanceCoreSet,
        IConfiguration configuration,
        INodeRuntimeSettings runtimeSettings,
        IOptions<SpawnOptions> spawnOptions,
        ToolApprovalCoordinator toolApprovalCoordinator,
        ApiToolCallBridge apiToolCallBridge,
        InvocationLifecycleTracker lifecycleTracker,
        IExternalProviderRegistry externalProviderRegistry,
        IServiceScopeFactory scopeFactory,
        ILogger<InvocationRunner> logger)
    {
        _lifecycleTracker = lifecycleTracker ?? throw new ArgumentNullException(nameof(lifecycleTracker));
        _toolApprovalCoordinator = toolApprovalCoordinator ?? throw new ArgumentNullException(nameof(toolApprovalCoordinator));
        _apiToolCallBridge = apiToolCallBridge ?? throw new ArgumentNullException(nameof(apiToolCallBridge));
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _invocationAgentFactory = invocationAgentFactory ?? throw new ArgumentNullException(nameof(invocationAgentFactory));
        _orchestrationAgentFactory = orchestrationAgentFactory ?? throw new ArgumentNullException(nameof(orchestrationAgentFactory));
        _runtimePackageValidator = runtimePackageValidator ?? throw new ArgumentNullException(nameof(runtimePackageValidator));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _localRuntimeWarmer = localRuntimeWarmer ?? throw new ArgumentNullException(nameof(localRuntimeWarmer));
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
        _toolRelevanceCoreSet = toolRelevanceCoreSet ?? throw new ArgumentNullException(nameof(toolRelevanceCoreSet));
        ArgumentNullException.ThrowIfNull(configuration);
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        ArgumentNullException.ThrowIfNull(spawnOptions);
        _spawnOptions = spawnOptions.Value;
        _externalProviderRegistry = externalProviderRegistry ?? throw new ArgumentNullException(nameof(externalProviderRegistry));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Read once at singleton construction from INodeRuntimeSettings (stored > appsettings seed > default) into plain fields the hot
        // streaming/cleanup loops read, so an operator edit applies on the next restart. The out-of-band Ollama:ChatModel override still wins, as in the chat-connection fallback.
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

        // Mark the turn's processing start (baseline for the pre-spawn latency + TTFT metrics) and open a coarse whole-turn span, so a silent
        // pre-spawn gap — a first send stalling seconds before the model spawn with no log lines — reads as timed child spans rather than a hang.
        var turnStartedTimestamp = Stopwatch.GetTimestamp();
        var harnessStartedTimestamp = context.HarnessStartedTimestamp ?? turnStartedTimestamp;
        using var turnActivity = NodeActivitySource.Source.StartActivity("chat.invocation.run");

        using (NodeActivitySource.Source.StartActivity("chat.invocation.validate_package"))
        {
            // Size cap OFF: this context is the node's own stored history plus node-authored synthetic context, and the inbound message was already
            // capped at its entry seam. Re-capping per turn would wedge every later turn of a conversation holding an oversized row; the budgeter below trims it instead.
            var validationResult = _runtimePackageValidator.Validate(package, enforceMessageSizeCap: false);
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(string.Join("; ", validationResult.Errors));
            }
        }

        var dispatcher = _eventDispatcher.Value;

        // Resolved ONCE per turn from the package's TimeoutSettings plus the node-level operational options, then flowed unchanged through both the
        // single-agent and orchestration paths so the two enforce identical policy. TurnPolicy's XML doc holds the composite budget (which timeout fires when).
        var turnPolicy = TurnPolicy.Resolve(package, _contextBudgetOptions, _resilienceOptions, _toolPipelineOptions, _maxPendingToolCallAge);

        _lifecycleTracker.RegisterActiveInvocation(package.InvocationId, turnPolicy.InvocationTimeout, cancellationToken);
        var activeInvocationCompletion = _lifecycleTracker.RegisterActiveInvocationCompletion(package.InvocationId);
        if (activeInvocationCompletion is null)
        {
            // The turn was admitted after the shutdown-drain snapshot: undo the registration above and surface a clean, classified failure
            // rather than running it into a drain that has stopped waiting. Reporting to the dispatcher is the whole surface.
            _lifecycleTracker.ClearActiveInvocation(package.InvocationId);
            _logger.LogInformation("Rejecting local invocation {InvocationId}: the node is draining for shutdown.", package.InvocationId);
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, NodeDrainingMessage, FailureCategory.Cancelled);
            return;
        }

        _apiToolCallBridge.SetToolResultTimeout(package.InvocationId, turnPolicy.ToolResultTimeout);

        using var providerBudgetScope = ProviderCallBudget.BeginScope(_providerCallBudgetOptions, harnessStartedTimestamp);
        var providerBudget = ProviderCallBudget.Current!;

        // Declared ahead of the terminal-telemetry local function below, which reads the readiness duration off it: a
        // local function may only capture a local that is already in scope where it is written.
        StreamState? stream = null;

        // Runs immediately BEFORE each of the three terminal reports and swallows its own faults, so telemetry can never replace a turn's real
        // classification. Counts only, no tool name. See docs/wiki/04-agent-mode.md ("Usage and estimated cost") for what rides it and why on every path.
        async Task ReportTerminalTelemetryAsync()
        {
            try
            {
                var efficiency = providerBudget.CaptureEfficiencySnapshot();
                await dispatcher.ReportToolSchemaTokensAsync(package.InvocationId, efficiency.ToolSchemaTokens, efficiency.MaximumToolSchemaTokens);
                await dispatcher.ReportTurnTelemetryAsync(package.InvocationId,
                                    stream?.ModelReadinessDurationMs is { } readinessMs ? (long)readinessMs : null,
                                    stream?.UsageSnapshot is { } turnUsage
                                        ? new TurnUsageTotals { InputTokens = turnUsage.InputTokens, OutputTokens = turnUsage.OutputTokens, TotalTokens = turnUsage.TotalTokens, ReasoningTokens = turnUsage.ReasoningTokens }
                                        : null);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not report the terminal telemetry for invocation {InvocationId}; the turn's outcome is unaffected.", package.InvocationId);
            }
        }

        string? invocationOutcome = null;

        try
        {
            var invocationToken = _lifecycleTracker.GetInvocationCancellationToken();
            // Read LIVE per turn, never captured in a field, INSIDE the try (a throw above it would fail the turn with no failure reported) and on
            // invocationToken, which is what an operator Cancel or the turn watchdog cancels. See docs/wiki/04-agent-mode.md §1.3 for the scope's placement.
            var toolRelevanceActive = await _runtimeSettings.GetToolRelevanceEnabledAsync(invocationToken)
                                      && !package.DisableToolRelevanceFilter;
            using var toolRelevanceScope = ToolRelevanceScope.BeginScope(toolRelevanceActive,
                toolRelevanceActive ? _toolRelevanceCoreSet.GetCoreToolNames() : FrozenSet<string>.Empty);
            if (toolRelevanceActive)
            {
                _logger.LogDebug("Tool-relevance filtering is ACTIVE for invocation {InvocationId}.", package.InvocationId);
            }

            ModelResolution modelResolution;
            using (NodeActivitySource.Source.StartActivity("chat.invocation.resolve_model"))
            {
                modelResolution = await ResolveModelAsync(package.ModelProfile, invocationToken);
            }

            var resolvedModel = modelResolution.Model;

            // `auto` resolves into a concrete {model, effort} HERE, between model resolution and the local warm, so an admitted small-model swap is what
            // gets warmed. Declared FIRST so `using` disposes it LAST, after the ledger reservation made inside it; AsyncServiceScope's `default` is unsafe to dispose.
            using var dispatchScope = ReasoningEffortNormalizer.Normalize(package.ReasoningEffort) is AutoReasoningEffort
                ? _scopeFactory.CreateScope()
                : null;

            ReasoningDispatchDecision? dispatchDecision = null;

            // The exact "auto" guard above is the whole byte-identical story for every other effort: no scope, no dispatcher
            // resolution, no request allocation, no node-side lookup. The package builder has already normalised the value.
            if (dispatchScope is not null)
            {
                dispatchDecision = await dispatchScope.ServiceProvider
                                                      .GetRequiredService<IReasoningEffortDispatcher>()
                                                      .DispatchAsync(BuildDispatchRequest(package, resolvedModel), invocationToken);
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

                // `package with { ... }` copies ConfigHash verbatim and the hash folds the AUTHORED effort, so two turns of one conversation dispatching to
                // different tiers share one hash and no resume is invalidated. No tier caps output (ReasoningDispatchDecision.MaxOutputTokens), so the send keeps its shape.
                package = package with
                {
                    ReasoningEffort = dispatched.Effort,
                    SupportsThinking = dispatched.SupportsThinking,
                    ReasoningBudgetEnforceable = dispatched.ReasoningBudgetEnforceable
                };

                // Onto the invocation state, so the terminalize write persists what `auto` resolved to with the envelope row: the tier plus the authored
                // effort (`auto` by the branch above), which separates the dispatched population from the pre-`auto` one. Every other turn's envelope carries nulls.
                await dispatcher.ReportEffortDispatchAsync(package.InvocationId, ReasoningTierLabels.For(dispatched.Tier), AutoReasoningEffort);
            }

            var modelWasSwapped = dispatchDecision is { } swapCandidate
                                  && !string.Equals(swapCandidate.Model, originalModel, StringComparison.Ordinal);

            // The one server-side record of what `auto` decided. The dispatcher takes no logger by design (its inputs are the user's message and the
            // turn's shape), so only its OUTPUT is logged: tier, stable kebab-case reason code, swapped flag. No signal value, model name or message text.
            if (dispatchDecision is { } logged)
            {
                _logger.LogInformation("Reasoning effort 'auto' dispatched for invocation {InvocationId}: tier {Tier}, reason {ReasonCode}, model swapped {ModelWasSwapped}.",
                    package.InvocationId, ReasoningTierLabels.For(logged.Tier), logged.ReasonCode, modelWasSwapped);
            }

            // The retry below re-enters RunSingleAgentAsync, which owns the tool-relevance drain and its ToolsFiltered notice, so running it twice would emit
            // that notice twice. A swap already requires no offered tools; stating the dependency here switches the retry off if a gate ever admits a tool-bearing swap.
            var swapRetryEligible = modelWasSwapped && package.AllowedTools.Count == 0;

            // Shared streaming state for both the single-agent and orchestration paths: response/reasoning accumulators, byte caps, monotonic sequence
            // counters, terminal usage snapshot. Both feed it through the same Emit* helpers, so transport, size cap, dispatcher reporting and ordering stay identical.
            stream = new StreamState
            {
                HarnessStartedTimestamp = harnessStartedTimestamp
            };

            var transport = new StreamTransport(this, dispatcher, package);

            // Surface the model-substitution fallback as a visible, non-fatal chat notice rather than a log line only,
            // which the transport (and therefore the dispatcher) now makes possible.
            if (modelResolution.Substituted)
            {
                await transport.EmitNoticeAsync(TurnNoticeKind.ModelSubstituted,
                    BuildModelSubstitutedNoticeMessage(modelResolution.RequestedModel, resolvedModel));
            }

            // A decision the user did not make must be visible, but a NORMAL no-swap turn stays silent (noise), and the detail is the reason CODE only.
            // A SWAPPED turn is silent here too: it may still fall back at the send boundary, and a turn carries exactly ONE effort notice, emitted once the send resolves.
            if (dispatchDecision is { } announced && announced.Tier != ReasoningTier.Normal && !modelWasSwapped)
            {
                await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                   BuildEffortDispatchedNoticeMessage(announced.Tier, announced.Effort, resolvedModel, swapped: false),
                                   announced.ReasonCode);
            }

            // Seed the per-root spawn context (Depth 0) so spawn_subagent enforces the fan-out and cloud-spawn caps against ONE shared root. It flows as an
            // AsyncLocal into the function-invocation pipeline running the tool body; disposal restores the prior ambient value, and a turn that never spawns pays one struct.
            using var spawnRoot = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns, resolvedModel);

            // BOTH models, not just the dispatched one: the send-boundary retry switches `resolvedModel` back to the original inside this scope, and a pin it
            // never resolved drops that fallback send onto the transport's weaker unpinned check. Identical ids de-duplicate, so the pin set is unchanged.
            var turnPins = await ExternalProviderInvocationPin
                                 .ResolveAsync(_externalProviderRegistry, [resolvedModel, originalModel], invocationToken);

            // Pin the binding this turn is authorized against once, up front: the provider re-reads configuration on every send, so an operator edit landing
            // mid-tool-loop would redirect the later ones. Opened here, not in the resolver — an AsyncLocal write never reaches its caller; no external model, no pin.
            using var externalBindingPin = ExternalProviderBindingPinScope.Begin(turnPins);

            // Seed the active conversation id into the same root tool-loop scope so the AgentHome tool gateway can stage this conversation's uploaded
            // attachments into the sandbox. Like the spawn context it flows as an AsyncLocal through the pipeline; disposal restores the prior ambient value.
            using var conversationScope = AgentRunConversationContext.BeginScope(package.ConversationId);

            // Warm the local model BEFORE the watched streaming pull, so a cold big-model load runs in its own size-aware supervisor window and is never
            // killed by the shorter stream-idle watchdog. Cloud and Ollama models no-op; the load is decoupled from this token, so a cancel only abandons the wait.
            var requestedContextTokens = turnPolicy.RequestedContextTokens ?? turnPolicy.ContextCapacityTokens;
            var localRuntime = await _localRuntimeWarmer.PrepareLocalRuntimeAsync(resolvedModel, dispatcher, package.InvocationId, stream, turnStartedTimestamp, invocationToken);
            var effectiveContextTokens = localRuntime.EffectiveContextTokens;

            // Fold the launched effective window into the turn policy (precedence: TurnPolicy.WithEffectiveContext) so the OUTER budgeter sizes history against the
            // real window and the INNER num_ctx budgeter resolves the same one. Captured BEFORE the fold: the swapped model's policy would size against a window the authorised model never had.
            var preWarmPolicy = turnPolicy;
            turnPolicy = turnPolicy.WithEffectiveContext(effectiveContextTokens);

            if (context.GenerationAdmissionPolicy is { } admissionPolicy)
            {
                // The chat path lets generation retry a failed warm so the provider boundary surfaces its authoritative error. An admission-gated caller
                // cannot: a null effective context would reject first and mask it, so rethrow the captured failure before consulting the policy.
                localRuntime.WarmFailure?.Throw();

                var admissionContext = new InvocationGenerationAdmissionContext
                {
                    InvocationId = package.InvocationId,
                    RequestedContextTokens = requestedContextTokens,
                    EffectiveContextTokens = effectiveContextTokens,
                    ModelId = resolvedModel,
                    ProviderName = localRuntime.ProviderName
                };
                var decision = await admissionPolicy.EvaluateAsync(admissionContext, invocationToken)
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
                // The OUTER conversation budgeter sizes against the effective window via the updated turnPolicy above; the same window is threaded per
                // participant, so each participant's INNER provider-round budgeter sizes against the window ITS model was launched with.
                await RunOrchestrationAsync(package, orchestrationSpec, resolvedModel, transport, stream, turnPolicy, effectiveContextTokens, invocationToken);
            }
            else
            {
                try
                {
                    await RunSingleAgentAsync(package, resolvedModel, transport, stream, turnPolicy, effectiveContextTokens, invocationToken);

                    // A swap served the turn. Announce it now, once, naming the model that actually ran. Every other
                    // turn already emitted its notice (or is a silent NORMAL one) before the send.
                    if (modelWasSwapped && dispatchDecision is { } served)
                    {
                        // The invocation state was seeded with the model the PACKAGE named, and the persisted message row and the envelope's provider
                        // attribution both read it there, so a swapped turn that does not correct it is measured against a model that never saw it.
                        await dispatcher.ReportServedModelAsync(package.InvocationId, resolvedModel);
                        await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                           BuildEffortDispatchedNoticeMessage(served.Tier, served.Effort, resolvedModel, swapped: true),
                                           served.ReasonCode);
                    }
                }
                // The fast model went away between the capacity probe and the send — profiled away, ejected, uninstalled, or it would not fit — and nothing has
                // reached the client, so re-run once on the authorised model. Keyed on "nothing streamed", which covers every such cause and cannot rot on provider wording.
                catch (Exception) when (swapRetryEligible && !stream.FirstOutputRecorded && !invocationToken.IsCancellationRequested)
                {
                    // RELEASE THE FAST RESERVATION FIRST: it books the small model's bytes and, on an Allow verdict, a launch admission and one of the
                    // loaded-process slots, so carrying it in double-books the ledger and can starve the original model's own spawn at the process cap. Dispose is idempotent.
                    fastReservation?.Dispose();
                    resolvedModel = originalModel;
                    package = package with
                    {
                        ReasoningEffort = FallbackDispatchEffort,
                        SupportsThinking = originalSupportsThinking,
                        ReasoningBudgetEnforceable = originalReasoningBudgetEnforceable
                    };

                    // Re-warm the ORIGINAL model and re-derive its window: the policy and effective context above were measured against the fast model's
                    // launched window, and carrying them in would size this turn's history — and the definition's num_ctx — against a window this model never had.
                    var retryRuntime = await _localRuntimeWarmer.PrepareLocalRuntimeAsync(resolvedModel, dispatcher, package.InvocationId, stream, turnStartedTimestamp, invocationToken);
                    var retryContextTokens = retryRuntime.EffectiveContextTokens;
                    var retryPolicy = preWarmPolicy.WithEffectiveContext(retryContextTokens);

                    await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                       BuildEffortDispatchedNoticeMessage(ReasoningTier.Fast, FallbackDispatchEffort, resolvedModel, swapped: false),
                                       ReasoningDispatchReasons.FastModelUnavailable);

                    // Exactly once. A second failure is a real failure and fails the turn normally.
                    await RunSingleAgentAsync(package, resolvedModel, transport, stream, retryPolicy, retryContextTokens, invocationToken);
                }
                // The swapped send failed with no fallback left — it had already streamed, the turn offers tools, or it is being cancelled. A turn carries exactly
                // ONE effort notice and the pre-send one is withheld for swapped turns, so without this a silently replaced model is never reported at all.
                catch (Exception) when (modelWasSwapped && dispatchDecision is { } failedSwap)
                {
                    // Names the model that actually served. No served-model report: the turn produced no answer to attribute and the fast model may have died
                    // before its first token, so the seeded (authorised) model stays on the failed row. The FAILURE is the outer handler's, as for any other turn.
                    await transport.EmitNoticeAsync(TurnNoticeKind.EffortDispatched,
                                       BuildEffortDispatchedNoticeMessage(failedSwap.Tier, failedSwap.Effort, resolvedModel, swapped: true),
                                       failedSwap.ReasonCode);
                    throw;
                }
            }

            // A turn cancelled by either watchdog, an operator stop or a shutdown drain as its stream ends with no further chunks returns NORMALLY without observing
            // the token (callbacks run in reverse registration order) and would persist as a SUCCESSFUL answer nobody received. The category still comes from the recorded origin.
            invocationToken.ThrowIfCancellationRequested();

            // Read the whole-turn wall-clock duration once. The same value rides the dispatcher report, so the
            // persisted tokens-per-second is computed from one authoritative measurement.
            var generationDurationMs = (long)stream.GenerationStopwatch.Elapsed.TotalMilliseconds;

            // Emit cumulative model token usage from the single per-turn finalize point, never the per-tool-loop usage-arrival site, which would
            // double-count across rounds. Content-free: counts tagged by the coarse provider dimension, model id and direction only.
            RecordTokenUsageMetric(stream, resolvedModel);

            if (stream.LastRoundUsage is null)
            {
                _logger.LogWarning("Terminal model usage was not reported for invocation {InvocationId} using model {ModelName}. Token fields will remain unknown.",
                    package.InvocationId,
                    resolvedModel);
            }

            await ReportTerminalTelemetryAsync();
            // LAST ROUND again: these reach InvocationState's token members, which the terminalize write persists onto the assistant message row, and which
            // the resume registry and the memory-extraction hook read. The turn's own cost rode ReportTurnTelemetryAsync a line above, onto the envelope row.
            await dispatcher.ReportInvocationCompletedAsync(package.InvocationId,
                stream.LastRoundUsage?.InputTokens,
                stream.LastRoundUsage?.OutputTokens,
                stream.LastRoundUsage?.TotalTokens,
                stream.LastRoundUsage?.ReasoningTokens,
                generationDurationMs,
                stream.FinishReason,
                stream.ToThroughput());
            invocationOutcome = "completed";
        }
        catch (OperationCanceledException) when (_lifecycleTracker.IsCurrentInvocation(package.InvocationId))
        {
            invocationOutcome = "cancelled";
            _lifecycleTracker.CancelPendingToolCalls(package.InvocationId);
            var cancellationOrigin = _lifecycleTracker.ResolveCancellationOrigin();
            var failureCategory = InvocationLifecycleTracker.ClassifyCancellation(cancellationOrigin);
            // One fixed, path-free sentence per cancellation cause. A single shared string would leave the node's message-request ceiling, an operator
            // stop and a detached-run reaper collection indistinguishable in the persisted failure, and a cancelled turn unattributable after the fact.
            var cancellationMessage = InvocationLifecycleTracker.DescribeCancellation(cancellationOrigin, turnPolicy.InvocationTimeout);
            // Count the cancellation by its cause (user | watchdog | shutdown). Distinct from InvocationFailedTotal — a cancel is an outcome, not a failure —
            // though an invocation-level timeout ("watchdog") is additionally reported as a Timeout failure below; the two metrics answer different questions.
            NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", InvocationLifecycleTracker.ClassifyCancellationMetricCategory(cancellationOrigin)));
            await ReportTerminalTelemetryAsync();
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, cancellationMessage, failureCategory);
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

            await ReportTerminalTelemetryAsync();
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, message, failureCategory);
        }
        finally
        {
            var efficiencyRecord = new InvocationEfficiencyRecord
            {
                InvocationId = package.InvocationId,
                Outcome = invocationOutcome ?? "failed",
                Provider = stream?.ProviderTag ?? "unknown",
                Orchestration = package.OrchestrationSpec is not null,
                TotalDurationMs = Stopwatch.GetElapsedTime(harnessStartedTimestamp).TotalMilliseconds,
                PreRunDurationMs = context.PreRunDurationMs,
                QueueDurationMs = context.QueueDurationMs,
                ModelReadinessDurationMs = stream?.ModelReadinessDurationMs,
                FirstOutputLatencyMs = stream?.FirstOutputLatencyMs,
                InputTokens = stream?.UsageSnapshot?.InputTokens,
                OutputTokens = stream?.UsageSnapshot?.OutputTokens,
                ReasoningTokens = stream?.UsageSnapshot?.ReasoningTokens,
                ProviderEfficiency = providerBudget.CaptureEfficiencySnapshot()
            };
            TryRecordInvocationEfficiency(efficiencyRecord, turnActivity);

            _apiToolCallBridge.CleanupStaleToolCalls(_maxPendingToolCallAge);
            _apiToolCallBridge.ClearToolResultTimeout(package.InvocationId);
            _lifecycleTracker.ClearActiveInvocation(package.InvocationId);
            _lifecycleTracker.CompleteActiveInvocation(package.InvocationId, activeInvocationCompletion);
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

    /// <summary>Emits the terminal token-usage counter for a completed turn, and nothing when the model reported no usage.</summary>
    /// <remarks>
    ///     Called once from the shared completion block, never the per-tool-loop usage-arrival site, so a multi-round tool
    ///     run counts its TURN TOTAL exactly once. Content-free: only the coarse provider dimension
    ///     (<see cref="StreamState.ProviderTag" />, local | remote), the resolved model id and the direction tag ride it.
    /// </remarks>
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
        // Budgeting runs at BOTH history growth points, so a long conversation or tool loop never overruns the window the provider was launched with. The gate
        // carries the "logged once" and "notice emitted once" flags, so an invocation reports at most once however many rounds trim; ApplyContextBudgetAsync throws when truncation is not enough.
        var budgetGate = new ContextBudgetNoticeGate();

        // Built once for the whole turn: the offer list is fixed for the invocation, and the budgeter's framing memo is
        // keyed on these string instances (see ApplyContextBudgetAsync).
        var toolBudgetDefinitions = BuildToolBudgetDefinitions(package);
        var seededMessages = await ApplyContextBudgetAsync(BuildChatMessages(package), package, toolBudgetDefinitions, resolvedModel, "initial-assembly", turnPolicy, transport, budgetGate);

        var definition = BuildInvocationDefinition(package, resolvedModel, seededMessages, effectiveContextTokens);
        // Coarse span over the MAF agent build — another pre-first-token stage. Disposed right after the
        // build so it does not enclose the streaming loop; the agent context keeps its normal await-using scope.
        var buildAgentActivity = NodeActivitySource.Source.StartActivity("chat.invocation.build_agent");
        await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken);
        buildAgentActivity?.Dispose();

        // Maps callId → the tool name plus what its Requested event carried, so a FunctionResultContent (which has no Name) resolves its tool from the
        // matching FunctionCallContent, and a re-emitted FunctionCallContent is recognised as a repeat before it pays another serialize + dispatch.
        var pendingLocalToolCalls = new Dictionary<string, RequestedToolCall>(StringComparer.Ordinal);

        // Surrogate ids for a provider that streams a BLANK CallId (Microsoft.Extensions.AI rejects a null one, so the empty string is the id-less shape): the FIRST
        // call to a tool keys on the tool NAME, the id the approval card resolves, and later id-less calls take "<name>#2". Holds the one still awaiting a result.
        var openSurrogateCallIds = new Dictionary<string, string>(StringComparer.Ordinal);

        // Ever-used, not currently-open, so an OVERLAPPING call is told apart by openSurrogateCallIds above. ONCE CLOSED, NEVER REUSED: a second SEQUENTIAL call cannot
        // land on the first call's key, where an identical payload would be swallowed as a streamed re-emission and a different one would merge its arguments with the last result.
        var usedSurrogateNames = new HashSet<string>(StringComparer.Ordinal);

        // The arrival-ordered queue the matching FunctionResultContent — which carries the call's own blank id — is paired back through. An approval-gated
        // tool on an id-less provider therefore correlates only its FIRST card: a limitation of having no id, and not one this widens.
        var pendingSurrogateResults = new Queue<(string Name, string CallId)>();
        var surrogateCallCount = 1;

        // Which tools this turn already surfaced a ToolDisabled notice for, so a model that keeps calling a disabled tool — each further call short-circuits
        // to the same "tool_disabled" result from ToolArgumentRepairAIFunction — is reported to the chat once per tool, not once per call.
        var notifiedDisabledTools = new HashSet<string>(StringComparer.Ordinal);

        // The conversation grows across approval-gated segments: a tool wrapped in ApprovalRequiredAIFunction makes FunctionInvokingChatClient surface a
        // ToolApprovalRequestContent and end the segment WITHOUT executing it, so the decision resumes threadlessly by replaying the folded segment plus the response.
        var currentMessages = new List<ChatMessage>(agentContext.SeedMessages);

        // One model turn can surface MORE than one approval request (parallel tool calls), so EVERY request in the segment is collected — a scalar would dangle
        // all but the last forever — deduped against a provider re-emitting one across chunks, on a namespaced key so a CallId and an approval Id cannot collide.
        var pendingApprovals = new List<ToolApprovalRequestContent>();
        var pendingApprovalKeys = new HashSet<string>(StringComparer.Ordinal);

        // Inter-chunk idle bound for every segment: a provider stalling between streamed chunks past the policy's stream-idle timeout has its send cancelled
        // by the watchdog, as a distinct Timeout-category failure. A non-positive value disables it, which the validator already rejects for a real package.
        var streamIdleTimeout = turnPolicy.StreamIdleTimeout;
        var streamIdleTimeoutMessage = turnPolicy.StreamIdleTimeoutMessage;

        // The pre-first-token retry + circuit breaker guards only the FIRST segment's send: once any chunk has streamed — and an approval-resume segment
        // follows earlier output by definition — a retry could duplicate output, so later segments run the provider send directly.
        var isFirstSegment = true;

        // The per-segment update list is retained ONLY to replay a folded segment on approval, and only an ApprovalRequiredAIFunction produces a ToolApprovalRequestContent.
        // The resolver ORs the registry pre-wrap into this tighten-only flag, so an all-false ClientLocal offer never wraps one; every other location is fail-closed here.
        var approvalPossible = package.AllowedTools.Any(static tool => tool.RequiresApproval || tool.Location != ToolLocation.ClientLocal);

        do
        {
            // Growth point (b): re-budget the approval-grown message list before each provider round — a cheap passthrough on the first iteration, and a bound on
            // the folded tool-call + approval history on a resume. The protected recent turns carry the in-flight round and are never trimmed, so the list stays valid.
            var budgetedMessages = await ApplyContextBudgetAsync(currentMessages, package, toolBudgetDefinitions, resolvedModel, "tool-loop", turnPolicy, transport, budgetGate);
            if (!ReferenceEquals(budgetedMessages, currentMessages))
            {
                currentMessages = budgetedMessages as List<ChatMessage> ?? [.. budgetedMessages];
            }

            pendingApprovals.Clear();
            pendingApprovalKeys.Clear();
            var segmentUpdates = new List<AgentResponseUpdate>();

            // The idle watchdog owns the token the provider call binds cancellation to, so an idle expiry actually cancels the send. The first segment also
            // runs through the pre-first-token retry + circuit breaker, which re-invokes this whole factory, so a fresh idle watchdog guards every attempt.
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

            await foreach (var update in segmentStream.WithCancellation(invocationToken))
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

                // Folded on ARRIVAL, not once per drained stream: a tool-calling turn is several llama-server requests inside ONE RunStreamingAsync, which
                // FunctionInvokingChatClient loops internally, so last-wins keeps only the final one. `timings` rides each request's LAST chunk; `timings_per_token`, which double-counts, stays off.
                stream.AddSegmentTimings(LlamaServerGenerationTimings.TryRead(update.RawRepresentation));

                // Reasoning text and the terminal usage snapshot are pulled in the SAME pass that fires the tool-call lifecycle events, so a streamed token
                // costs one scan. Local tools run inside FunctionInvokingChatClient, so detecting the call/result content here is what puts their lifecycle events on the SSE stream.
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
                                // A BLANK CallId takes an invocation-local surrogate (openSurrogateCallIds above), so a second same-name call is its own
                                // card and its result is never reported under an id the Requested event did not use.
                                var callName = functionCall.Name ?? string.Empty;
                                var openSurrogate = string.IsNullOrEmpty(functionCall.CallId) && openSurrogateCallIds.TryGetValue(callName, out var open)
                                    ? open
                                    : null;
                                var callId = openSurrogate ?? ResolveToolCallCardId(functionCall.CallId, functionCall.Name);

                                // A retired surrogate is never revived. This runs BEFORE the repeat checks below, so they compare against the fresh key
                                // and cannot mistake a genuine second call for a re-emission of the finished one.
                                if (string.IsNullOrEmpty(functionCall.CallId) && openSurrogate is null && usedSurrogateNames.Contains(callName))
                                {
                                    callId = string.Concat(callName, "#", (++surrogateCallCount).ToString(CultureInfo.InvariantCulture));
                                }

                                // A provider re-emitting the SAME call across streamed chunks would otherwise pay a Serialize + dispatch + SignalR frame per
                                // repeat and evict a real event from InvocationResumeRegistry's CAPPED tool history. Both guards are conservative: a distinct call still reports.
                                var isRepeatedCall = pendingLocalToolCalls.TryGetValue(callId, out var alreadyRequested)
                                                     && string.Equals(alreadyRequested.Name, callName, StringComparison.Ordinal);

                                // Same content instance re-emitted: identical by construction, so skip the serialize too.
                                if (isRepeatedCall && ReferenceEquals(alreadyRequested.Arguments, functionCall.Arguments))
                                {
                                    break;
                                }

                                var serializedArguments = functionCall.Arguments is not null
                                    ? JsonSerializer.Serialize(functionCall.Arguments)
                                    : null;

                                // Distinct instance, byte-identical payload: the event would be indistinguishable from the one already on the wire.
                                // Cache the new instance so the next repeat takes the cheaper reference check above.
                                if (isRepeatedCall && string.Equals(alreadyRequested.SerializedArguments, serializedArguments, StringComparison.Ordinal))
                                {
                                    pendingLocalToolCalls[callId] = new RequestedToolCall(alreadyRequested.Name, functionCall.Arguments, alreadyRequested.SerializedArguments);
                                    break;
                                }

                                if (string.IsNullOrEmpty(functionCall.CallId))
                                {
                                    // A surrogate still OPEN here means the payload DIFFERS from that call's. With no id, a second overlapping call and a
                                    // revision of the same card are indistinguishable, and collapsing them loses a call and its result where separating costs one extra card.
                                    if (openSurrogate is not null)
                                    {
                                        callId = string.Concat(callName, "#", (++surrogateCallCount).ToString(CultureInfo.InvariantCulture));
                                    }

                                    _ = usedSurrogateNames.Add(callName);
                                    openSurrogateCallIds[callName] = callId;
                                    pendingSurrogateResults.Enqueue((callName, callId));
                                }

                                // callName, not functionCall.Name: the local above null-coalesces the property the compiler still treats as maybe-null.
                                // The two are the same string whenever the provider gave a name at all.
                                pendingLocalToolCalls[callId] = new RequestedToolCall(callName, functionCall.Arguments, serializedArguments);

                                await transport.Dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
                                {
                                    InvocationId = package.InvocationId,
                                    ToolCallId = callId,
                                    ToolName = callName,
                                    Phase = ToolCallLifecyclePhase.Requested,
                                    Arguments = serializedArguments,
                                    RequiresApproval = false
                                });
                                break;

                            case FunctionResultContent functionResult:
                                // MEAI stamps the result with the CALL's id, so a blank call id yields a blank result id. Pair it with the oldest surrogate
                                // still awaiting one (results arrive in call order without ids): Completed under the empty string is dropped by every id-correlating consumer.
                                var resultCallId = functionResult.CallId ?? string.Empty;
                                if (string.IsNullOrEmpty(resultCallId) && pendingSurrogateResults.TryDequeue(out var surrogate))
                                {
                                    resultCallId = surrogate.CallId;

                                    // That call is closed: the next same-name call with no id opens its own card
                                    // instead of reviving this one.
                                    if (openSurrogateCallIds.TryGetValue(surrogate.Name, out var stillOpen)
                                        && string.Equals(stillOpen, resultCallId, StringComparison.Ordinal))
                                    {
                                        _ = openSurrogateCallIds.Remove(surrogate.Name);
                                    }
                                }

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
                                });

                                // ToolArgumentRepairAIFunction returns this structured result instead of throwing once repeated invalid-argument calls disable
                                // a tool for the rest of the run; without this it is visible only in the tool-result JSON. Once per tool — every further call returns the same marker.
                                if (IsToolDisabledResult(toolResultText) && notifiedDisabledTools.Add(toolName))
                                {
                                    await transport.EmitNoticeAsync(TurnNoticeKind.ToolDisabled,
                                        BuildToolDisabledNoticeMessage(toolName),
                                        toolName);
                                }

                                break;

                            case ToolApprovalRequestContent approvalRequest:
                                // FunctionInvokingChatClient surfaces this for an ApprovalRequiredAIFunction instead of executing the tool. Capture EVERY
                                // request in the segment, deduped across re-emitting chunks; the outer loop then runs each round-trip and resumes threadlessly.
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
                    await transport.EmitReasoningAsync(stream, thinkingBuilder.ToString());
                }

                if (string.IsNullOrEmpty(textChunk))
                {
                    continue;
                }

                await transport.EmitTextAsync(stream, textChunk);
            }

            // Drained at the end of the FIRST segment so it FOLLOWS the first assistant text, exactly as HistoryTruncated does: the hop cannot reach the
            // transport and leaves its counts on the ambient scope. Counts only, never a tool name; a first segment that THROWS emits nothing, since the line would read as the cause.
            if (isFirstSegment && ToolRelevanceScope.Current is { } relevanceState)
            {
                var hiddenToolCount = Volatile.Read(ref relevanceState.PendingNoticeHiddenCount);
                if (hiddenToolCount > 0)
                {
                    await transport.EmitNoticeAsync(TurnNoticeKind.ToolsFiltered,
                                       BuildToolsFilteredNoticeMessage(hiddenToolCount, Volatile.Read(ref relevanceState.PendingNoticeTotalCount)));
                }
            }

            // The first segment has drained; any resume segment past this point follows earlier output and must not be
            // retried (a retry there would replay already-streamed chunks).
            isFirstSegment = false;

            if (pendingApprovals.Count > 0)
            {
                // Fold the streamed segment into messages (the assistant tool calls plus the approval requests), run EACH round-trip over the existing transport,
                // which presents one at a time, then replay history plus ONE user message carrying every response, from which FunctionInvokingChatClient executes or rejects each call.
                var foldedMessages = segmentUpdates.ToAgentResponse().Messages;
                currentMessages.AddRange(foldedMessages);

                var approvalResponses = new List<AIContent>(pendingApprovals.Count);
                foreach (var approvalRequest in pendingApprovals)
                {
                    // ask_user rides the approval seam for its BLOCKING behaviour, not a risk verdict (AskUserToolHandler): its round-trip collects an ANSWER
                    // and then always approves, so the framework executes the tool and the handler returns that answer. Every other tool keeps the approve/deny path.
                    if (ToolApprovalCoordinator.IsUserQuestionRequest(approvalRequest))
                    {
                        var answerNote = await _toolApprovalCoordinator.RequestUserAnswerAsync(package, approvalRequest, _lifecycleTracker.SetInvocationDeadline, invocationToken);
                        approvalResponses.Add(approvalRequest.CreateResponse(approved: true, answerNote));
                        continue;
                    }

                    var approved = await _toolApprovalCoordinator.RequestToolApprovalAsync(package, approvalRequest, _lifecycleTracker.SetInvocationDeadline, invocationToken);
                    approvalResponses.Add(approvalRequest.CreateResponse(approved, approved ? "Approved by user." : "Rejected by user."));
                }

                currentMessages.Add(new ChatMessage(ChatRole.User, approvalResponses));
            }
        } while (pendingApprovals.Count > 0);
    }

    /// <summary>Runs the orchestration path: the package's spec becomes the definition a MAF handoff workflow drives.</summary>
    /// <remarks>
    ///     Each participant's offer list is bridged with the same <c>InvocationToolBridge</c> switch the single-agent path
    ///     uses, and the normalized <c>OrchestrationUpdate</c> stream maps onto the same transport, cap, sequence and
    ///     approval plumbing. The workflow owns multi-hop tool invocation; this loop only fans deltas out and round-trips approvals.
    /// </remarks>
    private async Task RunOrchestrationAsync(RuntimePackage package,
        OrchestrationSpec spec,
        string resolvedModel,
        StreamTransport transport,
        StreamState stream,
        TurnPolicy turnPolicy,
        int? effectiveContextTokens,
        CancellationToken invocationToken)
    {
        var definition = await BuildOrchestrationDefinitionAsync(package, spec, resolvedModel, effectiveContextTokens, transport, invocationToken);

        // A participant runs on its OWN model, which the turn-level pin does not cover, so its sends would fall to the transport's weaker unpinned check while
        // the workflow carries node-local tool results between participants. One scope for all of them: the workflow interleaves participants in this single async flow.
        var resolvedParticipantPins = await ExternalProviderInvocationPin
                                            .ResolveAsync(_externalProviderRegistry,
                                                definition.Participants.Select(participant => participant.ModelId),
                                                invocationToken);
        using var participantPins = ExternalProviderBindingPinScope.Begin(resolvedParticipantPins);

        // The workflow seed is budgeted exactly the way the single-agent path budgets its initial assembly (see TurnPolicy), so a long
        // conversation cannot silently overrun the window any participant is launched with.
        var budgetGate = new ContextBudgetNoticeGate();
        var seed = await ApplyContextBudgetAsync(BuildChatMessages(package), package, BuildToolBudgetDefinitions(package), resolvedModel, "orchestration-seed", turnPolicy, transport, budgetGate);

        await using var session = await _orchestrationAgentFactory.CreateAsync(definition, seed, invocationToken);

        // Drain to the natural end of WatchAsync rather than breaking on the first TerminalOutput: the session drives the workflow as the stream is pulled and
        // ends it right after, so a full drain is the documented terminator and an early break could truncate a later-superstep delta. It adds no idle latency.
        string? activeParticipantKey = null;
        await foreach (var update in session.WatchAsync(invocationToken))
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
                    await transport.EmitReasoningAsync(stream, update.Text);
                    break;

                case OrchestrationUpdateKind.TextDelta when !string.IsNullOrEmpty(update.Text):
                    await transport.EmitTextAsync(stream, update.Text);
                    break;

                case OrchestrationUpdateKind.ApprovalRequest when update.RequestId is { } requestId:
                    // Surface the approval over the existing transport, then answer it on the HELD run and keep draining — the tool executes in a later
                    // superstep. The description names the tool rather than the opaque id, so the card matches the single-agent UX.
                    var pendingApproval = ToApprovalRequest(update);
                    var approvalDescription = $"Tool '{ApprovalToolName(update)}' requires approval before it runs.";
                    var approved = await _toolApprovalCoordinator.RequestToolApprovalAsync(package, pendingApproval, _lifecycleTracker.SetInvocationDeadline, invocationToken, approvalDescription);
                    await session.RespondToApprovalAsync(requestId,
                        approved,
                        approved ? "Approved by user." : "Rejected by user.",
                        invocationToken);
                    break;

                case OrchestrationUpdateKind.Failure:
                    // Map a workflow failure onto the agent-runtime failure path. The raw MAF executor detail is logged server-side only and the client gets
                    // a CONSTANT safe message, because MapFailure does not redact a plain InvalidOperationException and framework internals must not leak.
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
        var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
        await RunAsync(context, cancellationToken);
    }

    /// <summary>Derives the id that keys a tool-call card: the wire call id when present, otherwise the tool name.</summary>
    /// <remarks>
    ///     Microsoft.Extensions.AI rejects a null call id in the <c>FunctionCallContent</c>/<c>FunctionResultContent</c> constructors
    ///     (verified against 10.9.0), so the empty string IS the id-less shape, and resolving it to the tool name is what lets such a
    ///     call be recorded at all — every consumer correlating a call with its result drops a blank id. The streaming and approval
    ///     lifecycles share it, so both resolve one id per call; the streaming loop adds a <c>name#N</c> surrogate only for a second
    ///     id-less call while the first is still open. Internal rather than private purely as a test seam.
    /// </remarks>
    internal static string ResolveToolCallCardId(string? callId, string? toolName) =>
        string.IsNullOrEmpty(callId) ? toolName ?? string.Empty : callId;

    /// <summary>A local tool call seen requested on the stream but not yet resulted: its name plus the arguments as they arrived.</summary>
    /// <remarks>
    ///     Kept so a repeated call for the same id is detected and the result is attributed to its tool. Distinct from
    ///     <c>PendingToolCall</c>, which tracks a worker tool call's approval and result completions.
    /// </remarks>
    private readonly record struct RequestedToolCall(string Name, object? Arguments, string? SerializedArguments);
}
