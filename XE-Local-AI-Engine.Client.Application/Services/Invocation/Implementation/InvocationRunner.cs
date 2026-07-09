namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
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
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

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
    private readonly IProviderStreamResilience _providerStreamResilience;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IRuntimePackageValidator _runtimePackageValidator;
    private readonly SpawnOptions _spawnOptions;
    private readonly Lock _syncRoot = new();

    // The effective tool-result wait budget for each active invocation, seeded from the package's
    // ToolCallTimeoutSeconds when RunAsync starts. ExecuteApiToolCallAsync (which only carries the invocation id) reads
    // it here so a package-scoped tool timeout wins over the node-global _maxPendingToolCallAge; absent an entry (a
    // tool call outside an active invocation) it falls back to the node-global age.
    private readonly ConcurrentDictionary<Guid, TimeSpan> _toolResultTimeoutsByInvocation = new();

    private Guid? _currentInvocationId;

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

        var validationResult = _runtimePackageValidator.Validate(package);
        if (!validationResult.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validationResult.Errors));
        }

        var sender = _hubSender.Value;
        var dispatcher = _eventDispatcher.Value;
        var shouldSendHubMessages = !IsLocalLoopbackInvocation(package);
        var sendEncrypted = shouldSendHubMessages && context.IsEncrypted;
        var sendPlain = shouldSendHubMessages && !context.IsEncrypted;

        RegisterActiveInvocation(package.InvocationId, package.Timeouts.InvocationTimeoutSeconds, cancellationToken);
        var activeInvocationCompletion = RegisterActiveInvocationCompletion(package.InvocationId);
        _toolResultTimeoutsByInvocation[package.InvocationId] = ResolveConfiguredToolResultTimeout(package.Timeouts);

        try
        {
            var invocationToken = GetInvocationCancellationToken();
            var resolvedModel = await ResolveModelAsync(package.ModelProfile, invocationToken).ConfigureAwait(false);

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

            // Seed the per-root-invocation spawn context (Depth 0) for this turn so the spawn_subagent tool (when the
            // agent calls it) enforces the fan-out and cloud-spawn caps against ONE shared root. The context flows as an
            // AsyncLocal into the function-invocation pipeline that runs the tool body; disposal restores the prior
            // ambient value. A turn that never spawns pays only a struct allocation.
            using var spawnRoot = SpawnContext.BeginRoot(_spawnOptions.MaxConcurrentSpawns, _spawnOptions.MaxCloudSpawns);

            // Seed the active conversation id into the same root tool-loop scope so the AgentHome tool gateway can stage
            // this conversation's uploaded attachments into the sandbox. Like the spawn context it flows as an
            // AsyncLocal through the function-invocation pipeline; disposal restores the prior ambient value.
            using var conversationScope = AgentRunConversationContext.BeginScope(package.ConversationId);

            // Branch: a package carrying a compiled orchestration spec drives the handoff workflow; everything else is
            // the unchanged single-agent loop. Both accumulate into `stream`, then share the completion block below.
            if (package.OrchestrationSpec is { } orchestrationSpec)
            {
                await RunOrchestrationAsync(package, orchestrationSpec, resolvedModel, transport, stream, invocationToken).ConfigureAwait(false);
            }
            else
            {
                await RunSingleAgentAsync(package, resolvedModel, transport, stream, invocationToken).ConfigureAwait(false);
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
        var activeInvocationTasks = _activeInvocationCompletions.Values.Select(static completion => completion.Task).ToArray();
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

    // The single-agent path. Drives one ChatClientAgent over an approval-gated do/while loop, accumulating into
    // `stream` through the shared transport so the streaming behavior matches the orchestration path byte-for-byte.
    private async Task RunSingleAgentAsync(RuntimePackage package,
        string resolvedModel,
        StreamTransport transport,
        StreamState stream,
        CancellationToken invocationToken)
    {
        // Deterministic input-context budgeting is applied at BOTH history growth points so a long conversation (or a
        // long tool-calling loop) never overruns the window the provider is launched with. The single trim warning is
        // gated by this flag so an invocation logs at most once regardless of how many rounds trim.
        var budgetTrimLogged = false;
        var seededMessages = ApplyContextBudget(BuildChatMessages(package), package, "initial-assembly", ref budgetTrimLogged);

        var definition = BuildInvocationDefinition(package, resolvedModel, seededMessages);
        await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken).ConfigureAwait(false);

        // Maps callId → toolName so FunctionResultContent (which has no Name) can resolve the tool name
        // from the earlier FunctionCallContent with the matching CallId.
        var pendingLocalToolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);

        // The conversation grows across approval-gated segments. A high-risk ClientLocal tool wrapped in
        // ApprovalRequiredAIFunction makes FunctionInvokingChatClient surface a ToolApprovalRequestContent and
        // end the segment WITHOUT executing the tool. We carry the decision over the existing approval transport
        // and resume threadlessly (session: null) by replaying the folded segment messages plus the approval
        // response. A segment that surfaces no approval request completes the run.
        var currentMessages = new List<ChatMessage>(agentContext.SeedMessages);
        ToolApprovalRequestContent? pendingApproval;

        // Inter-chunk idle bound for every segment: if the provider stalls between streamed chunks for longer than the
        // package's StreamIdleTimeoutSeconds the watchdog cancels the send and surfaces a distinct (Timeout-category)
        // failure. A non-positive value disables it (the validator already rejects one for a real package).
        var streamIdleTimeout = TimeSpan.FromSeconds(package.Timeouts.StreamIdleTimeoutSeconds);
        var streamIdleTimeoutMessage =
            $"Streaming stalled: no output received for {package.Timeouts.StreamIdleTimeoutSeconds}s (stream idle timeout).";

        // The pre-first-token retry + circuit breaker only guards the FIRST segment's send: once any chunk has streamed
        // (or a later approval-resume segment begins, which by definition follows earlier output) a retry could
        // duplicate output, so subsequent segments run the provider send directly.
        var isFirstSegment = true;

        do
        {
            // Growth point (b): before each provider round, re-budget the (approval-)grown message list. On the first
            // iteration this is a cheap passthrough (the seed was already budgeted); on an approval resume it bounds the
            // folded tool-call + approval history. The protected recent turns — which carry the in-flight round — are
            // never trimmed, so a budgeted list is still valid to send.
            var budgetedMessages = ApplyContextBudget(currentMessages, package, "tool-loop", ref budgetTrimLogged);
            if (!ReferenceEquals(budgetedMessages, currentMessages))
            {
                currentMessages = budgetedMessages as List<ChatMessage> ?? [.. budgetedMessages];
            }

            pendingApproval = null;
            var segmentUpdates = new List<AgentResponseUpdate>();

            // Each provider send is guarded by the inter-chunk idle watchdog (the watchdog owns the token the provider
            // call binds cancellation to, so an idle expiry actually cancels the send). The first segment additionally
            // runs through the pre-first-token retry + circuit breaker; the retry re-invokes this whole factory, so a
            // fresh idle watchdog guards every attempt.
            IAsyncEnumerable<AgentResponseUpdate> ProviderSend(CancellationToken sendToken)
            {
                return StreamIdleWatchdog.WithIdleTimeout(
                    innerToken => agentContext.Agent.RunStreamingAsync(currentMessages, session: null, agentContext.RunOptions, innerToken),
                    streamIdleTimeout,
                    streamIdleTimeoutMessage,
                    sendToken);
            }

            var segmentStream = isFirstSegment
                ? _providerStreamResilience.ExecuteStreamingAsync(resolvedModel, ProviderSend, invocationToken)
                : ProviderSend(invocationToken);

            await foreach (var update in segmentStream.ConfigureAwait(false))
            {
                segmentUpdates.Add(update);
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

                                await transport.Dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
                                {
                                    InvocationId = package.InvocationId,
                                    ToolCallId = resultCallId,
                                    ToolName = toolName,
                                    Phase = ToolCallLifecyclePhase.Completed,
                                    Result = functionResult.Result?.ToString(),
                                    IsError = functionResult.Exception is not null
                                }).ConfigureAwait(false);
                                break;

                            case ToolApprovalRequestContent approvalRequest:
                                // FunctionInvokingChatClient surfaces this for an ApprovalRequiredAIFunction instead of
                                // executing the tool. Capture it; the segment ends and the outer loop runs the approval
                                // round-trip, then resumes threadlessly with the decision.
                                pendingApproval = approvalRequest;
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

            if (pendingApproval is not null)
            {
                // Fold the streamed segment into messages (carries the assistant tool-call + approval request),
                // run the approval round-trip over the existing transport, then replay history + the approval
                // response so FunctionInvokingChatClient reconstructs and executes (or rejects) the tool call.
                var foldedMessages = segmentUpdates.ToAgentResponse().Messages;
                var approved = await RequestToolApprovalAsync(package, pendingApproval, invocationToken).ConfigureAwait(false);
                currentMessages.AddRange(foldedMessages);
                currentMessages.Add(new ChatMessage(ChatRole.User,
                    [pendingApproval.CreateResponse(approved, approved ? "Approved by user." : "Rejected by user.")]));
            }
        } while (pendingApproval is not null);
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
        CancellationToken invocationToken)
    {
        var definition = await BuildOrchestrationDefinitionAsync(package, spec, resolvedModel, invocationToken).ConfigureAwait(false);
        var seed = BuildChatMessages(package);

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

    private TaskCompletionSource RegisterActiveInvocationCompletion(Guid invocationId)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_activeInvocationCompletions.TryAdd(invocationId, completion))
        {
            throw new InvalidOperationException($"Invocation {invocationId} is already tracked as active.");
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

    // The tool-result wait budget for a package: its ToolCallTimeoutSeconds when set, else the node-global age. Read
    // once when the invocation starts and stashed per invocation id so ExecuteApiToolCallAsync can resolve it.
    private TimeSpan ResolveConfiguredToolResultTimeout(TimeoutSettings timeouts)
    {
        return timeouts.ToolCallTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(timeouts.ToolCallTimeoutSeconds)
            : _maxPendingToolCallAge;
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

    private void RegisterActiveInvocation(Guid invocationId, int timeoutSeconds, CancellationToken cancellationToken)
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
            invocationCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

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
