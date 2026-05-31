namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;

/// <summary>
///     Represents invocation runner.
/// </summary>
public sealed class InvocationRunner : IInvocationRunner
{
    private const string AgentToolCallFailureMessage = "Worker tool execution failed.";
    private const string ModelUnavailableMessage = "Selected model is not installed on this node.";
    private const string ProviderUnavailableMessage = "Provider unreachable.";
    private const string OrchestrationFailureMessage = "Orchestration run failed.";

    private static readonly Regex FrameworkExceptionNamePattern =
        new(@"\b(?:Microsoft|System)(?:\.[A-Za-z_][A-Za-z0-9_]*)*\.[A-Za-z_][A-Za-z0-9_]*Exception\b|\b(?:AgentException|ChatClientAgentException)\b", RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _activeInvocationCompletions = new();

    private readonly ICapabilityReporter _capabilityReporter;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly string _defaultModel;
    private readonly IEnvelopeCryptoService _envelopeCryptoService;
    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;
    private readonly Lazy<IHubMessageSender> _hubSender;
    private readonly IInvocationAgentFactory _invocationAgentFactory;
    private readonly IOrchestrationAgentFactory _orchestrationAgentFactory;
    private readonly ILogger<InvocationRunner> _logger;
    private readonly TimeSpan _maxPendingToolCallAge;
    private readonly int _maxResponseSizeBytes;
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls = new(StringComparer.Ordinal);
    private readonly IRuntimePackageValidator _runtimePackageValidator;
    private readonly object _syncRoot = new();

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
        IDeadLetterStore deadLetterStore,
        IConfiguration configuration,
        IOptions<WorkerNodeOptions> workerOptions,
        ILogger<InvocationRunner> logger)
    {
        _hubSender = hubSender ?? throw new ArgumentNullException(nameof(hubSender));
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _invocationAgentFactory = invocationAgentFactory ?? throw new ArgumentNullException(nameof(invocationAgentFactory));
        _orchestrationAgentFactory = orchestrationAgentFactory ?? throw new ArgumentNullException(nameof(orchestrationAgentFactory));
        _envelopeCryptoService = envelopeCryptoService ?? throw new ArgumentNullException(nameof(envelopeCryptoService));
        _runtimePackageValidator = runtimePackageValidator ?? throw new ArgumentNullException(nameof(runtimePackageValidator));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _deadLetterStore = deadLetterStore ?? throw new ArgumentNullException(nameof(deadLetterStore));
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(workerOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _defaultModel = configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                        ?? configuration.GetValue<string>("Ollama:ChatModel")
                        ?? throw new InvalidOperationException("Agent:LocalChat:DefaultModel is required for invocation execution.");
        _maxResponseSizeBytes = workerOptions.Value.MaxResponseSizeMb * 1024 * 1024;
        _maxPendingToolCallAge = TimeSpan.FromMinutes(workerOptions.Value.MaxPendingToolCallAgeMinutes);
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

            if (sendEncrypted)
            {
                await sender.SendEncryptedCompletedAsync(_envelopeCryptoService.EncryptCompleted(package.ConversationId,
                        context.MessageId,
                        context.EpochVersion,
                        context.EpochKey.Span,
                        Encoding.UTF8.GetBytes(stream.ResponseBuilder.ToString()),
                        stream.Sequence,
                        stream.UsageSnapshot?.ToTokenCounts() ?? new Dictionary<string, long>(),
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
                    true,
                    stream.ReasoningSequence + 1,
                    invocationToken).ConfigureAwait(false);
                await sender.SendTokenStreamChunkAsync(package.InvocationId,
                    string.Empty,
                    true,
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
                    ReasoningTokens = stream.UsageSnapshot?.ReasoningTokens
                }, invocationToken).ConfigureAwait(false);
            }

            await dispatcher.ReportInvocationCompletedAsync(package.InvocationId,
                stream.UsageSnapshot?.InputTokens,
                stream.UsageSnapshot?.OutputTokens,
                stream.UsageSnapshot?.TotalTokens,
                stream.UsageSnapshot?.ReasoningTokens).ConfigureAwait(false);
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
            ClearActiveInvocation(package.InvocationId);
            CompleteActiveInvocation(package.InvocationId, activeInvocationCompletion);
            await TryReportCapabilitiesAfterInvocationAsync(package.InvocationId).ConfigureAwait(false);
        }
    }

    // The unchanged single-agent path, extracted verbatim from the former RunAsync body. Drives one ChatClientAgent
    // over an approval-gated do/while loop, accumulating into `stream` through the shared transport so the streaming
    // behavior is identical to before P5.
    private async Task RunSingleAgentAsync(RuntimePackage package,
        string resolvedModel,
        StreamTransport transport,
        StreamState stream,
        CancellationToken invocationToken)
    {
        var definition = BuildInvocationDefinition(package, resolvedModel);
        await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken).ConfigureAwait(false);

        // Maps callId → toolName so FunctionResultContent (which has no Name) can resolve the tool name
        // from the earlier FunctionCallContent with the matching CallId.
        var pendingLocalToolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);

        // The conversation grows across approval-gated segments. A high-risk ClientLocal tool wrapped in
        // ApprovalRequiredAIFunction makes FunctionInvokingChatClient surface a ToolApprovalRequestContent and
        // end the segment WITHOUT executing the tool. We carry the decision over the existing approval transport
        // and resume threadlessly (session: null) by replaying the folded segment messages plus the approval
        // response (the proven P0 gate shape). A segment that surfaces no approval request completes the run.
        var currentMessages = new List<ChatMessage>(agentContext.SeedMessages);
        ToolApprovalRequestContent? pendingApproval;

        do
        {
            pendingApproval = null;
            var segmentUpdates = new List<AgentResponseUpdate>();

            await foreach (var update in agentContext.Agent.RunStreamingAsync(currentMessages, null, agentContext.RunOptions, invocationToken).ConfigureAwait(false))
            {
                segmentUpdates.Add(update);
                var textChunk = update.Text;
                var thinkingChunk = string.Concat(update.Contents?.OfType<TextReasoningContent>()
                                                        .Select(t => t.Text) ?? Enumerable.Empty<string>());
                var usage = update.Contents?.OfType<UsageContent>().LastOrDefault()?.Details;
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

                // Local (ClientSide) tools execute via FunctionInvokingChatClient and never reach
                // ExecuteApiToolCallAsync, so their lifecycle events would otherwise be lost.
                // Detect FunctionCallContent / FunctionResultContent in streaming updates and
                // fire the matching lifecycle phases so the SSE stream carries tool-call events.
                if (update.Contents is { Count: > 0 })
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent functionCall)
                        {
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
                        }
                        else if (content is FunctionResultContent functionResult)
                        {
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
                        }
                        else if (content is ToolApprovalRequestContent approvalRequest)
                        {
                            // FunctionInvokingChatClient surfaces this for an ApprovalRequiredAIFunction instead of
                            // executing the tool. Capture it; the segment ends and the outer loop runs the approval
                            // round-trip, then resumes threadlessly with the decision.
                            pendingApproval = approvalRequest;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(thinkingChunk))
                {
                    await transport.EmitReasoningAsync(stream, thinkingChunk, invocationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(textChunk))
                {
                    continue;
                }

                await transport.EmitTextAsync(stream, textChunk, invocationToken).ConfigureAwait(false);
            }

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
        }
        while (pendingApproval is not null);
    }

    // The loop-P5 orchestration path. Compiles the package's OrchestrationSpec into the MAF-agnostic
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

                default:
                    // ApprovalRequest with no RequestId, or empty text deltas: nothing to forward.
                    break;
            }
        }
    }

    // Compiles the loopback OrchestrationSpec into the .AI.Agent OrchestrationAgentDefinition: each participant's
    // model is resolved to a concrete installed model (its pinned profile, else the turn's resolved model), and its
    // projected offer list is bridged into AITools with the SAME switch BuildInvocationTools uses (ApiSide → real
    // bridge over ExecuteApiToolCallAsync; ClientLocal → name-only placeholder the factory swaps for the registry
    // executable). The seed history rides on the workflow input, not per participant.
    private async Task<OrchestrationAgentDefinition> BuildOrchestrationDefinitionAsync(RuntimePackage package,
        OrchestrationSpec spec,
        string resolvedModel,
        CancellationToken invocationToken)
    {
        var participants = new List<OrchestrationParticipant>(spec.Participants.Count);
        foreach (var participant in spec.Participants)
        {
            var participantModel = await ResolveModelAsync(participant.ModelId ?? resolvedModel, invocationToken).ConfigureAwait(false);
            participants.Add(new OrchestrationParticipant
            {
                Key = participant.Key,
                Name = participant.Name,
                Description = participant.Description,
                Instructions = participant.Instructions,
                ModelId = participantModel,
                ReasoningEffort = participant.ReasoningEffort,
                Tools = BuildParticipantTools(package, participant.Tools)
            });
        }

        var triage = participants.FirstOrDefault(p => string.Equals(p.Key, spec.TriageParticipantKey, StringComparison.Ordinal))
                     ?? throw new InvalidOperationException("Orchestration spec triage participant is not present in the participant set.");

        var edges = spec.Edges
                        .Select(static edge => new OrchestrationEdge
                        {
                            FromKey = edge.FromKey,
                            ToKey = edge.ToKey,
                            Reason = edge.Reason
                        })
                        .ToArray();

        return new OrchestrationAgentDefinition
        {
            Triage = triage,
            Participants = participants,
            Edges = edges,
            EmitStreamingUpdates = true,
            MaxTurnsPerAgent = spec.MaxTurnsPerAgent,
            ReturnToPrevious = spec.ReturnToPrevious
        };
    }

    private IReadOnlyList<AITool> BuildParticipantTools(RuntimePackage package, IReadOnlyList<AllowedToolDto> tools)
    {
        return
        [
            .. tools.Select(tool => tool.Location switch
            {
                ToolLocation.ApiSide => InvocationToolBridge.Create(tool.Name,
                    tool.Description,
                    tool.ParameterSchema,
                    (arguments, cancellationToken) => ExecuteApiToolCallAsync(package.InvocationId, tool.Name, arguments, tool.RequiresApproval, cancellationToken)),
                ToolLocation.ClientLocal => InvocationToolBridge.CreateOfferPlaceholder(tool.Name),
                _ => throw new InvalidOperationException($"Unsupported tool location: {tool.Location}")
            })
        ];
    }

    private static ToolApprovalRequestContent ToApprovalRequest(OrchestrationUpdate update)
    {
        // The orchestration session correlates the decision by its own RequestId; the bridged transport only needs a
        // human-readable description, so synthesize a minimal request carrying the tool name awaiting approval.
        var callId = update.RequestId ?? Guid.NewGuid().ToString("N");
        return new ToolApprovalRequestContent(callId, new FunctionCallContent(callId, ApprovalToolName(update)));
    }

    private static string ApprovalToolName(OrchestrationUpdate update)
    {
        return string.IsNullOrWhiteSpace(update.ToolName) ? "tool" : update.ToolName;
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
        return ExecuteApiToolCallAsync(invocationId, toolName, parameters, true, cancellationToken);
    }

    public async Task RunAsync(RuntimePackage package, CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.Create(package, Guid.Empty, 0, ReadOnlyMemory<byte>.Empty);
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

            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

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
            _logger.LogWarning(exception, "Failed to report capabilities after invocation {InvocationId} completed.", invocationId);
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

    private async Task<string> ResolveModelAsync(string? requestedModel, CancellationToken cancellationToken)
    {
        if (await _capabilityReporter.VerifyOllamaAndModelAsync(requestedModel, cancellationToken).ConfigureAwait(false))
        {
            return string.IsNullOrWhiteSpace(requestedModel) ? _defaultModel : requestedModel.Trim();
        }

        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            throw new InvalidOperationException("Ollama is unavailable or the default model is not installed.");
        }

        _logger.LogWarning("Requested model '{RequestedModel}' could not be verified. Falling back to '{FallbackModel}'.",
            requestedModel,
            _defaultModel);

        return _defaultModel;
    }

    private InvocationAgentDefinition BuildInvocationDefinition(RuntimePackage package, string resolvedModel)
    {
        var messages = BuildChatMessages(package);

        return new InvocationAgentDefinition(resolvedModel,
            package.ResolvedSystemPrompt,
            BuildInvocationTools(package),
            messages,
            package.ReasoningEffort);
    }

    private static IReadOnlyList<ChatMessage> BuildChatMessages(RuntimePackage package)
    {
        return package.ConversationContext
                      .OrderBy(message => message.SortOrder)
                      .Select(static message =>
                      {
                          var contents = new List<AIContent>();
                          if (!string.IsNullOrEmpty(message.Thinking))
                          {
                              contents.Add(new TextReasoningContent(message.Thinking));
                          }

                          contents.Add(new TextContent(message.Content));
                          return new ChatMessage(MapRole(message.Role), contents);
                      })
                      .ToList();
    }

    private IReadOnlyList<AITool> BuildInvocationTools(RuntimePackage package)
    {
        // The runtime package only carries the OFFER list. Api-side tools get a real bridge that round-trips to the
        // platform; client-local (catalog) tools get a name-only placeholder, and the invocation factory swaps it for
        // the matching executable from IAgentToolRegistry before the agent runs.
        return
        [
            .. package.AllowedTools.Select(tool => tool.Location switch
            {
                ToolLocation.ApiSide => InvocationToolBridge.Create(tool.Name,
                    tool.Description,
                    tool.ParameterSchema,
                    (arguments, cancellationToken) => ExecuteApiToolCallAsync(package.InvocationId, tool.Name, arguments, tool.RequiresApproval, cancellationToken)),
                ToolLocation.ClientLocal => InvocationToolBridge.CreateOfferPlaceholder(tool.Name),
                _ => throw new InvalidOperationException($"Unsupported tool location: {tool.Location}")
            })
        ];
    }

    private static ChatRole MapRole(MessageRole role)
    {
        return role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.User => ChatRole.User,
            MessageRole.Assistant => ChatRole.Assistant,
            MessageRole.Tool => ChatRole.Tool,
            _ => throw new InvalidOperationException($"Unsupported message role: {role}")
        };
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

    private static (FailureCategory Category, string Message) MapFailure(Exception exception)
    {
        return exception switch
        {
            TimeoutException timeoutException => (FailureCategory.Timeout, timeoutException.Message),
            WorkerToolCallException => (FailureCategory.AgentToolCall, AgentToolCallFailureMessage),
            NotSupportedException notSupportedException => (FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(notSupportedException.Message)),
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("Response size exceeded", StringComparison.Ordinal) =>
                (FailureCategory.Unexpected, invalidOperationException.Message),
            HttpRequestException httpRequestException when httpRequestException.StatusCode == HttpStatusCode.NotFound =>
                (FailureCategory.ModelUnavailable, ModelUnavailableMessage),
            HttpRequestException => (FailureCategory.ProviderUnreachable, ProviderUnavailableMessage),
            _ when IsAgentRuntimeException(exception) => (FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(exception.Message)),
            _ => (FailureCategory.Unexpected, TruncateUnexpectedMessage(exception.Message))
        };
    }

    private static string RedactAgentRuntimeMessage(string message)
    {
        var sanitizedMessage = FrameworkExceptionNamePattern.Replace(message, string.Empty);
        sanitizedMessage = Regex.Replace(sanitizedMessage, @"\s{2,}", " ").Trim(' ', ':', '-', ',', ';');

        return string.IsNullOrWhiteSpace(sanitizedMessage)
            ? "Agent runtime error."
            : $"Agent runtime error: {sanitizedMessage}";
    }

    private static bool IsAgentRuntimeException(Exception exception)
    {
        var type = exception.GetType();
        var fullName = type.FullName ?? string.Empty;

        return fullName.StartsWith("Microsoft.Agents.AI.", StringComparison.Ordinal)
               || string.Equals(type.Name, "AgentException", StringComparison.Ordinal)
               || string.Equals(type.Name, "ChatClientAgentException", StringComparison.Ordinal)
               || messageContainsFrameworkTypeName(exception.Message);

        static bool messageContainsFrameworkTypeName(string message)
        {
            return FrameworkExceptionNamePattern.IsMatch(message);
        }
    }

    private static string TruncateUnexpectedMessage(string message)
    {
        return message.Length > 512 ? message[..512] : message;
    }

    private static bool IsLocalLoopbackInvocation(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return package.RequestedCapabilities?.Any(static capability => string.Equals(capability, LocalChatLoopbackDefaults.RequestedCapability, StringComparison.Ordinal)) == true;
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
        CancellationTokenSource? invocationCancellationTokenSource = null;

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

    // The mutable streaming accumulator shared by the single-agent and orchestration paths: the response/reasoning
    // builders, the byte totals (against _maxResponseSizeBytes), the monotonic sequence counters the transport sends,
    // and the terminal usage snapshot. Carried by reference into the branch methods so the post-stream completion
    // block in RunAsync reads the final state.
    private sealed class StreamState
    {
        public StringBuilder ResponseBuilder { get; } = new();

        public StringBuilder ReasoningBuilder { get; } = new();

        public UsageSnapshot? UsageSnapshot { get; set; }

        public long Sequence { get; set; }

        public long ReasoningSequence { get; set; }

        public int TotalResponseBytes { get; set; }

        public int TotalReasoningBytes { get; set; }
    }

    // The single emit path both branches use: it appends to the accumulator, enforces the response/reasoning byte
    // caps, advances the sequence counter, reports the chunk to the dispatcher, and sends it over the encrypted or
    // plain hub transport. Keeping this one place guarantees the orchestration path streams byte-for-byte like the
    // single-agent path.
    private sealed class StreamTransport
    {
        private readonly InvocationRunner _runner;
        private readonly IHubMessageSender _sender;
        private readonly InvocationExecutionContext _context;
        private readonly RuntimePackage _package;
        private readonly bool _sendEncrypted;
        private readonly bool _sendPlain;

        public StreamTransport(InvocationRunner runner,
            IHubMessageSender sender,
            IWorkerEventDispatcher dispatcher,
            InvocationExecutionContext context,
            RuntimePackage package,
            bool sendEncrypted,
            bool sendPlain)
        {
            _runner = runner;
            _sender = sender;
            Dispatcher = dispatcher;
            _context = context;
            _package = package;
            _sendEncrypted = sendEncrypted;
            _sendPlain = sendPlain;
        }

        public IWorkerEventDispatcher Dispatcher { get; }

        public async Task EmitReasoningAsync(StreamState stream, string thinkingChunk, CancellationToken cancellationToken)
        {
            stream.TotalReasoningBytes += Encoding.UTF8.GetByteCount(thinkingChunk);
            if (stream.TotalReasoningBytes > _runner._maxResponseSizeBytes)
            {
                throw new InvalidOperationException($"Reasoning size exceeded maximum of {_runner._maxResponseSizeBytes / (1024 * 1024)}MB");
            }

            stream.ReasoningSequence++;
            stream.ReasoningBuilder.Append(thinkingChunk);

            await Dispatcher.ReportInvocationThinkingChunkAsync(_package.InvocationId, thinkingChunk).ConfigureAwait(false);

            if (_sendEncrypted)
            {
                await _sender.SendEncryptedChunkAsync(_runner._envelopeCryptoService.EncryptChunk(_package.ConversationId,
                        _context.MessageId,
                        _context.EpochVersion,
                        _context.EpochKey.Span,
                        Encoding.UTF8.GetBytes(thinkingChunk),
                        stream.ReasoningSequence,
                        EncryptedChunkEnvelopeV1.ReasoningKind),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (_sendPlain)
            {
                await _sender.SendReasoningStreamChunkAsync(_package.InvocationId,
                    thinkingChunk,
                    false,
                    stream.ReasoningSequence,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task EmitTextAsync(StreamState stream, string textChunk, CancellationToken cancellationToken)
        {
            stream.Sequence++;
            stream.TotalResponseBytes += Encoding.UTF8.GetByteCount(textChunk);

            if (stream.TotalResponseBytes > _runner._maxResponseSizeBytes)
            {
                throw new InvalidOperationException($"Response size exceeded maximum of {_runner._maxResponseSizeBytes / (1024 * 1024)}MB");
            }

            stream.ResponseBuilder.Append(textChunk);

            await Dispatcher.ReportInvocationStreamChunkAsync(_package.InvocationId, textChunk).ConfigureAwait(false);

            if (_sendEncrypted)
            {
                await _sender.SendEncryptedChunkAsync(_runner._envelopeCryptoService.EncryptChunk(_package.ConversationId,
                        _context.MessageId,
                        _context.EpochVersion,
                        _context.EpochKey.Span,
                        Encoding.UTF8.GetBytes(textChunk),
                        stream.Sequence),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (_sendPlain)
            {
                await _sender.SendTokenStreamChunkAsync(_package.InvocationId,
                    textChunk,
                    false,
                    stream.Sequence,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed record UsageSnapshot(int? InputTokens, int? OutputTokens, int? ReasoningTokens, int? TotalTokens)
    {
        public static UsageSnapshot From(UsageDetails usage)
        {
            var inputTokens = ToNullableInt(usage.InputTokenCount);
            var outputTokens = ToNullableInt(usage.OutputTokenCount);
            var reasoningTokens = ToNullableInt(usage.ReasoningTokenCount);
            var totalTokens = ToNullableInt(usage.TotalTokenCount)
                              ?? SumIfAny(inputTokens, outputTokens, reasoningTokens);

            return new UsageSnapshot(inputTokens, outputTokens, reasoningTokens, totalTokens);
        }

        public Dictionary<string, long> ToTokenCounts()
        {
            var counts = new Dictionary<string, long>();
            AddIfPresent(counts, "inputTokens", InputTokens);
            AddIfPresent(counts, "outputTokens", OutputTokens);
            AddIfPresent(counts, "reasoningTokens", ReasoningTokens);
            AddIfPresent(counts, "totalTokens", TotalTokens);
            return counts;
        }

        private static void AddIfPresent(Dictionary<string, long> counts, string key, int? value)
        {
            if (value is not null)
            {
                counts[key] = value.Value;
            }
        }

        private static int? SumIfAny(params int?[] values)
        {
            return values.Any(static value => value is not null)
                ? values.Sum(static value => value ?? 0)
                : null;
        }

        private static int? ToNullableInt(long? value)
        {
            return value is null ? null : checked((int)value.Value);
        }
    }

    private sealed record PendingToolCall(
        Guid InvocationId,
        DateTimeOffset CreatedAt,
        TaskCompletionSource<bool> ApprovalCompletion,
        TaskCompletionSource<ToolCallResultEvent> ResultCompletion);

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
