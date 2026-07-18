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
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
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

    // A new local turn admitted after shutdown drain has begun (GPTAUD-21). Surfaced as a clean Cancelled-category
    // failure — the node is going away — rather than being run into a drain that has already stopped waiting for it.
    private const string NodeDrainingMessage = "The node is shutting down and is not accepting new requests.";

    // The budgeter's hard-stop (see ApplyContextBudgetAsync): history still exceeds the resolved context budget after
    // the two-pass truncation. A fixed, path-free constant carrying no token counts, model names, or content.
    private const string ContextBudgetExceededMessage =
        "Conversation exceeds the model's context window even after truncation — start a new chat or switch to a larger-context model.";

    private static readonly Regex FrameworkExceptionNamePattern =
        new(@"\b(?:Microsoft|System)(?:\.[A-Za-z_][A-Za-z0-9_]*)*\.[A-Za-z_][A-Za-z0-9_]*Exception\b|\b(?:AgentException|ChatClientAgentException)\b", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _activeInvocationCompletions = new();

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
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls = new(StringComparer.Ordinal);
    private readonly ProviderCallBudgetOptions _providerCallBudgetOptions;
    private readonly IProviderStreamResilience _providerStreamResilience;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly ProviderResilienceOptions _resilienceOptions;
    private readonly IRuntimePackageValidator _runtimePackageValidator;
    private readonly SpawnOptions _spawnOptions;
    private readonly Lock _syncRoot = new();
    private readonly AgentToolPipelineOptions _toolPipelineOptions;

    // The effective tool-result wait budget for each active invocation, seeded from the package's
    // ToolCallTimeoutSeconds when RunAsync starts. ExecuteApiToolCallAsync (which only carries the invocation id) reads
    // it here so a package-scoped tool timeout wins over the node-global _maxPendingToolCallAge; absent an entry (a
    // tool call outside an active invocation) it falls back to the node-global age.
    private readonly ConcurrentDictionary<Guid, TimeSpan> _toolResultTimeoutsByInvocation = new();

    private Guid? _currentInvocationId;

    // Set once (never reset) when shutdown drain begins, guarded by _syncRoot. A local invocation that reaches
    // admission after this is set is rejected (GPTAUD-21): it registers AFTER the drain snapshot and would otherwise
    // become an untracked active run the drain never waits for.
    private bool _draining;

    private CancellationTokenSource? _invocationCancellationTokenSource;
    private bool _timeoutTriggered;
    private bool _userCancelRequested;

    public InvocationRunner(Lazy<IHubMessageSender> hubSender,
        Lazy<IWorkerEventDispatcher> eventDispatcher,
        IInvocationAgentFactory invocationAgentFactory,
        IOrchestrationAgentFactory orchestrationAgentFactory,
        IEnvelopeCryptoService envelopeCryptoService,
        IRuntimePackageValidator runtimePackageValidator,
        ICapabilityReporter capabilityReporter,
        ILocalModelProviderResolver providerResolver,
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
        ILogger<InvocationRunner> logger)
    {
        _hubSender = hubSender ?? throw new ArgumentNullException(nameof(hubSender));
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _invocationAgentFactory = invocationAgentFactory ?? throw new ArgumentNullException(nameof(invocationAgentFactory));
        _orchestrationAgentFactory = orchestrationAgentFactory ?? throw new ArgumentNullException(nameof(orchestrationAgentFactory));
        _envelopeCryptoService = envelopeCryptoService ?? throw new ArgumentNullException(nameof(envelopeCryptoService));
        _runtimePackageValidator = runtimePackageValidator ?? throw new ArgumentNullException(nameof(runtimePackageValidator));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
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
        using var turnActivity = NodeActivitySource.Source.StartActivity("chat.invocation.run");

        using (NodeActivitySource.Source.StartActivity("chat.invocation.validate_package"))
        {
            var validationResult = _runtimePackageValidator.Validate(package);
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
            // Shutdown drain has started and this is a local turn admitted after the drain snapshot (GPTAUD-21). Undo the
            // registration above and surface a clean, classified failure instead of running it into a drain that has
            // stopped waiting. A local turn sends no hub messages, so reporting to the dispatcher is the whole surface.
            ClearActiveInvocation(package.InvocationId);
            _logger.LogInformation("Rejecting local invocation {InvocationId}: the node is draining for shutdown.", package.InvocationId);
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, NodeDrainingMessage, FailureCategory.Cancelled).ConfigureAwait(false);
            return;
        }

        _toolResultTimeoutsByInvocation[package.InvocationId] = turnPolicy.ToolResultTimeout;

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
            var stream = new StreamState();

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

            // Seed the per-invocation provider-budget scope (HIGH-005) into the same root tool-loop flow. The innermost
            // pipeline hop reads it as an AsyncLocal to re-budget every inner tool-loop round and MAF participant round
            // and to enforce the cumulative provider-call ceilings; disposal restores the prior ambient value. A turn
            // that never loops pays only one pass-through budget check.
            using var providerBudgetScope = ProviderCallBudget.BeginScope(_providerCallBudgetOptions);

            // AUD4-01: warm the local model to readiness BEFORE the watched streaming pull begins, so a cold big-model
            // load happens in its OWN size-aware window (owned by the supervisor) and is never killed by the shorter
            // stream-idle watchdog — the primary cause of the audited "big model can never load through chat" hang.
            // Cloud (Codex/Azure) and Ollama models are a no-op here. The load is decoupled from this caller's token in
            // the supervisor, so a user who cancels merely abandons the wait while the load continues in the background.
            var effectiveContextTokens = await PrepareLocalRuntimeAsync(resolvedModel, dispatcher, package.InvocationId, stream, turnStartedTimestamp, invocationToken).ConfigureAwait(false);

            // AUD4-02: fold the launched effective context window into the turn policy so the OUTER conversation
            // budgeter sizes history against the real window. The per-send num_ctx override still wins; an unknown
            // window (cloud/Ollama/not-yet-started) keeps the configured default. The same value is threaded into the
            // agent definition below so the INNER provider-round budgeter (num_ctx side channel) agrees.
            turnPolicy = ApplyEffectiveContext(turnPolicy, package, effectiveContextTokens);

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
        }
        catch (OperationCanceledException) when (IsCurrentInvocation(package.InvocationId))
        {
            CancelPendingToolCalls(package.InvocationId);
            var failureCategory = ClassifyCancellation();
            // AUD4-19: count the cancellation by its cause (user | watchdog | shutdown). Distinct from InvocationFailedTotal:
            // a cancel is an outcome, not a failure. An invocation-level timeout ("watchdog") is additionally surfaced as a
            // Timeout failure below via ReportInvocationFailedAsync — the two metrics answer different questions.
            NodeMetrics.InvocationCancelledTotal.Add(1, new KeyValuePair<string, object?>("category", ClassifyCancellationMetricCategory()));
            await dispatcher.ReportInvocationFailedAsync(package.InvocationId, "Invocation timed out or was cancelled", failureCategory).ConfigureAwait(false);
            if (shouldSendHubMessages)
            {
                await TrySendFailureAsync(sender, context, "Invocation timed out or was cancelled", failureCategory).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
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
        // fence and the snapshot and become an untracked active run (GPTAUD-21).
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
        CancellationTokenSource? invocationCancellationTokenSource = null;

        lock (_syncRoot)
        {
            if (_currentInvocationId == invocationId)
            {
                invocationCancellationTokenSource = _invocationCancellationTokenSource;
                _userCancelRequested = true;
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
        }

        invocationCancellationTokenSource?.Cancel();

        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                removedPendingToolCall.ApprovalCompletion.TrySetCanceled();
                removedPendingToolCall.ResultCompletion.TrySetCanceled();
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

    public void ResolveApprovalResult(ApprovalResolvedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_pendingToolCalls.TryGetValue(evt.RequestId, out var pendingToolCall))
        {
            pendingToolCall.ApprovalCompletion.TrySetResult(evt.Approved);
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
    private async Task<int?> PrepareLocalRuntimeAsync(string resolvedModel,
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
            return null;
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
            RecordReadiness(startedUtc, "cancelled");
            readinessActivity?.SetTag("outcome", "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            // Readiness failed (e.g. model incompatible / OOM). Record it and let the streaming send surface the real,
            // classified failure — it hits the same supervisor path and produces the proper error.
            RecordReadiness(startedUtc, "failed");
            readinessActivity?.SetTag("outcome", "failed");
            _logger.LogWarning(exception, "Model warm failed for invocation {InvocationId}; the streaming send will surface the classified failure.", invocationId);
            return null;
        }

        var durationMs = RecordReadiness(startedUtc, "ready");
        readinessActivity?.SetTag("outcome", "ready");

        // The model is ready: measure TTFT from HERE (the first emitted chunk records against this baseline).
        stream.ModelReadyTimestamp = Stopwatch.GetTimestamp();
        _logger.LogInformation("Local model ready for invocation {InvocationId} after {ElapsedMs:F0} ms; arming the stream-idle watchdog for generation.", invocationId, durationMs);

        // Phase: generating (the model is ready; streaming begins under the stream-idle watchdog).
        await dispatcher.ReportInvocationPhaseAsync(invocationId, InvocationRuntimePhase.Generating).ConfigureAwait(false);

        // AUD4-02: with the model now ready, read the effective per-slot context window it actually loaded so the turn's
        // budgeters + the num_ctx side channel size against the REAL window (llama.cpp's -c) rather than the app default.
        // Best-effort — a null here just keeps the configured default. A cancellation propagates (the turn is terminating).
        return await ResolveEffectiveContextTokensAsync(provider, resolvedModel, invocationId, cancellationToken).ConfigureAwait(false);
    }

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
    ///     Folds the launched effective context window into the turn policy. Precedence: a per-send <c>num_ctx</c>
    ///     override (already resolved into <see cref="TurnPolicy.ContextCapacityTokens" /> by <see cref="TurnPolicy.Resolve" />)
    ///     wins and is left untouched; otherwise a known effective window replaces the configured default so the outer
    ///     conversation budgeter sizes against the real window. An unknown window leaves the policy unchanged.
    /// </summary>
    private static TurnPolicy ApplyEffectiveContext(TurnPolicy turnPolicy, RuntimePackage package, int? effectiveContextTokens)
    {
        if (package.SamplingOptions?.NumCtx is > 0 || effectiveContextTokens is not > 0)
        {
            return turnPolicy;
        }

        return turnPolicy with
        {
            ContextCapacityTokens = effectiveContextTokens.Value
        };
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
        var seededMessages = await ApplyContextBudgetAsync(BuildChatMessages(package), package, "initial-assembly", turnPolicy, transport, budgetGate).ConfigureAwait(false);

        var definition = BuildInvocationDefinition(package, resolvedModel, seededMessages, effectiveContextTokens);
        // Coarse span over the MAF agent build (AUD4-23) — another pre-first-token stage. Disposed right after the
        // build so it does not enclose the streaming loop; the agent context keeps its normal await-using scope.
        var buildAgentActivity = NodeActivitySource.Source.StartActivity("chat.invocation.build_agent");
        await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken).ConfigureAwait(false);
        buildAgentActivity?.Dispose();

        // Maps callId → toolName so FunctionResultContent (which has no Name) can resolve the tool name
        // from the earlier FunctionCallContent with the matching CallId.
        var pendingLocalToolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);

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
        // the earlier requests forever, GPTAUD-02). The dedup key is namespaced so a CallId and an approval Id can never
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
        // surfaces. That can happen only when the package offered tools: InvocationToolResolver yields no executable
        // tools for an empty offer list, so a tool-less turn can never wrap a tool in ApprovalRequiredAIFunction and
        // thus never surfaces a ToolApprovalRequestContent. Skip retaining every streamed update of a plain answer.
        var approvalPossible = package.AllowedTools.Count > 0;

        do
        {
            // Growth point (b): before each provider round, re-budget the (approval-)grown message list. On the first
            // iteration this is a cheap passthrough (the seed was already budgeted); on an approval resume it bounds the
            // folded tool-call + approval history. The protected recent turns — which carry the in-flight round — are
            // never trimmed, so a budgeted list is still valid to send.
            var budgetedMessages = await ApplyContextBudgetAsync(currentMessages, package, "tool-loop", turnPolicy, transport, budgetGate).ConfigureAwait(false);
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
                                var callId = functionCall.CallId ?? functionCall.Name;
                                pendingLocalToolCallNames[callId] = functionCall.Name;

                                await transport.Dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
                                {
                                    InvocationId = package.InvocationId,
                                    ToolCallId = callId,
                                    ToolName = functionCall.Name,
                                    Phase = ToolCallLifecyclePhase.Requested,
                                    Arguments = functionCall.Arguments is not null
                                        ? JsonSerializer.Serialize(functionCall.Arguments)
                                        : null,
                                    RequiresApproval = false
                                }).ConfigureAwait(false);
                                break;

                            case FunctionResultContent functionResult:
                                var resultCallId = functionResult.CallId ?? string.Empty;
                                var toolName = pendingLocalToolCallNames.TryGetValue(resultCallId, out var name)
                                    ? name
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
        var seed = await ApplyContextBudgetAsync(BuildChatMessages(package), package, "orchestration-seed", turnPolicy, transport, budgetGate).ConfigureAwait(false);

        await using var session = await _orchestrationAgentFactory.CreateAsync(definition, seed, invocationToken).ConfigureAwait(false);

        // Drain to the natural end of WatchAsync rather than breaking on the first TerminalOutput: the factory's
        // session drives the workflow as the stream is pulled and ends the stream right after the terminal output, so
        // a full drain is the documented terminator (an early break would risk truncating a later-superstep delta in
        // autonomous/multi-turn shapes). The terminal output carries no further deltas, so this adds no idle latency.
        await foreach (var update in session.WatchAsync(invocationToken).ConfigureAwait(false))
        {
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

                // Surface the pending approval on the LOCAL chat stream (UX-01). This API-tool path emits its
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
    /// </summary>
    private async Task<bool> RequestToolApprovalAsync(RuntimePackage package,
        ToolApprovalRequestContent approvalRequest,
        CancellationToken cancellationToken,
        string? descriptionOverride = null)
    {
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

            // Surface the pending approval on the LOCAL chat stream (UX-01). The CallId is derived the SAME way the
            // tool-call-requested lifecycle event derives it (CallId, falling back to the tool name) so the browser can
            // attach the Approve/Deny controls to the matching tool-call card. In desktop/local mode there is no worker
            // hub to resolve the approval, so the loopback resolve endpoint feeds ResolveApprovalResult below. ToolCall
            // is the base ToolCallContent (CallId only); the concrete FunctionCallContent carries the tool name.
            var approvalToolName = (approvalRequest.ToolCall as FunctionCallContent)?.Name;
            var approvalCallId = string.IsNullOrEmpty(approvalRequest.ToolCall.CallId)
                ? approvalToolName ?? string.Empty
                : approvalRequest.ToolCall.CallId;
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

            return await approvalCompletion.Task.WaitAsync(approvalTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
    }

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
            var reportTask = _capabilityReporter.ReportToApiAsync();
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
    // turn (GPTAUD-21) — the completion add and the draining check happen under _syncRoot so they are serialized with
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

    private FailureCategory ClassifyCancellation()
    {
        lock (_syncRoot)
        {
            if (_userCancelRequested)
            {
                return FailureCategory.Cancelled;
            }

            return _timeoutTriggered ? FailureCategory.Timeout : FailureCategory.Cancelled;
        }
    }

    // The cancellation cause for the invocation_cancelled_total metric (AUD4-19): an explicit user cancel, the
    // invocation-level timeout firing ("watchdog"), or neither — an external token cancellation, i.e. host shutdown.
    // Reads the same flags ClassifyCancellation does, under the same lock.
    private string ClassifyCancellationMetricCategory()
    {
        lock (_syncRoot)
        {
            if (_userCancelRequested)
            {
                return "user";
            }

            return _timeoutTriggered ? "watchdog" : "shutdown";
        }
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
                removedPendingToolCall.ApprovalCompletion.TrySetCanceled();
                removedPendingToolCall.ResultCompletion.TrySetCanceled();
            }
        }
    }

    private void RegisterActiveInvocation(Guid invocationId, TimeSpan invocationTimeout, CancellationToken cancellationToken)
    {
        CancellationTokenSource? invocationCancellationTokenSource = null;

        try
        {
            invocationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            invocationCancellationTokenSource.Token.Register(() =>
            {
                lock (_syncRoot)
                {
                    if (!_userCancelRequested)
                    {
                        _timeoutTriggered = true;
                    }
                }
            });
            invocationCancellationTokenSource.CancelAfter(invocationTimeout);

            lock (_syncRoot)
            {
                if (_currentInvocationId is not null)
                {
                    throw new InvalidOperationException("Worker is busy with another invocation");
                }

                _currentInvocationId = invocationId;
                _userCancelRequested = false;
                _timeoutTriggered = false;
                _invocationCancellationTokenSource = invocationCancellationTokenSource;
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
            _currentInvocationId = null;
            _userCancelRequested = false;
            _timeoutTriggered = false;
        }

        invocationCancellationTokenSource?.Dispose();
    }

    /// <summary>
    ///     Exception raised for worker tool call failures.
    /// </summary>
    public sealed class WorkerToolCallException : Exception
    {
        public WorkerToolCallException(string toolName, string message, Exception? innerException = null)
            : base($"Tool call '{toolName}' failed: {message}", innerException)
        {
        }
    }
}
