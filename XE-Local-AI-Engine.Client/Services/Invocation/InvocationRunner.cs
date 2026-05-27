namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Invocation;
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

public sealed class InvocationRunner : IInvocationRunner
{
    private const string AgentToolCallFailureMessage = "Worker tool execution failed.";
    private const string ModelUnavailableMessage = "Selected model is not installed on this node.";
    private const string ProviderUnavailableMessage = "Provider unreachable.";

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

    public async Task RunAsync(Models.RuntimePackage package, CancellationToken cancellationToken = default)
    {
        using var context = InvocationExecutionContext.Create(package, Guid.Empty, 0, ReadOnlyMemory<byte>.Empty);
        await RunAsync(context, cancellationToken).ConfigureAwait(false);
    }

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
            var responseBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var streamedChunkCount = 0;
            var streamedReasoningChunkCount = 0;
            UsageSnapshot? usageSnapshot = null;
            long sequence = 0;
            long reasoningSequence = 0;
            var totalResponseBytes = 0;
            var totalReasoningBytes = 0;

            if (shouldSendHubMessages)
            {
                await sender.SendInvocationAcceptedAsync(package.InvocationId, invocationToken).ConfigureAwait(false);
            }

            var definition = BuildInvocationDefinition(package, resolvedModel);
            await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken).ConfigureAwait(false);

            // Maps callId → toolName so FunctionResultContent (which has no Name) can resolve the tool name
            // from the earlier FunctionCallContent with the matching CallId.
            var pendingLocalToolCallNames = new Dictionary<string, string>(StringComparer.Ordinal);

            await foreach (var update in agentContext.Agent.RunStreamingAsync(agentContext.SeedMessages, null, agentContext.RunOptions, invocationToken).ConfigureAwait(false))
            {
                var textChunk = update.Text;
                var thinkingChunk = string.Concat(update.Contents?.OfType<TextReasoningContent>()
                                                        .Select(t => t.Text) ?? Enumerable.Empty<string>());
                var usage = update.Contents?.OfType<UsageContent>().LastOrDefault()?.Details;
                if (usage is not null)
                {
                    usageSnapshot = UsageSnapshot.From(usage);
                    _logger.LogDebug("Received terminal usage for invocation {InvocationId}: input={InputTokens}, output={OutputTokens}, reasoning={ReasoningTokens}, total={TotalTokens}.",
                        package.InvocationId,
                        usageSnapshot.InputTokens,
                        usageSnapshot.OutputTokens,
                        usageSnapshot.ReasoningTokens,
                        usageSnapshot.TotalTokens);
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

                            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
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

                            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
                            {
                                InvocationId = package.InvocationId,
                                ToolCallId = resultCallId,
                                ToolName = toolName,
                                Phase = ToolCallLifecyclePhase.Completed,
                                Result = functionResult.Result?.ToString(),
                                IsError = functionResult.Exception is not null
                            }).ConfigureAwait(false);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(thinkingChunk))
                {
                    totalReasoningBytes += Encoding.UTF8.GetByteCount(thinkingChunk);
                    if (totalReasoningBytes > _maxResponseSizeBytes)
                    {
                        throw new InvalidOperationException($"Reasoning size exceeded maximum of {_maxResponseSizeBytes / (1024 * 1024)}MB");
                    }

                    streamedReasoningChunkCount++;
                    reasoningSequence++;
                    reasoningBuilder.Append(thinkingChunk);

                    await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, thinkingChunk).ConfigureAwait(false);

                    if (sendEncrypted)
                    {
                        await sender.SendEncryptedChunkAsync(_envelopeCryptoService.EncryptChunk(package.ConversationId,
                                context.MessageId,
                                context.EpochVersion,
                                context.EpochKey.Span,
                                Encoding.UTF8.GetBytes(thinkingChunk),
                                reasoningSequence,
                                EncryptedChunkEnvelopeV1.ReasoningKind),
                            invocationToken).ConfigureAwait(false);
                    }
                    else if (sendPlain)
                    {
                        await sender.SendReasoningStreamChunkAsync(package.InvocationId,
                            thinkingChunk,
                            false,
                            reasoningSequence,
                            invocationToken).ConfigureAwait(false);
                    }
                }

                if (string.IsNullOrEmpty(textChunk))
                {
                    continue;
                }

                streamedChunkCount++;
                sequence++;
                totalResponseBytes += Encoding.UTF8.GetByteCount(textChunk);

                if (totalResponseBytes > _maxResponseSizeBytes)
                {
                    throw new InvalidOperationException($"Response size exceeded maximum of {_maxResponseSizeBytes / (1024 * 1024)}MB");
                }

                responseBuilder.Append(textChunk);

                await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, textChunk).ConfigureAwait(false);

                if (sendEncrypted)
                {
                    await sender.SendEncryptedChunkAsync(_envelopeCryptoService.EncryptChunk(package.ConversationId,
                            context.MessageId,
                            context.EpochVersion,
                            context.EpochKey.Span,
                            Encoding.UTF8.GetBytes(textChunk),
                            sequence),
                        invocationToken).ConfigureAwait(false);
                }
                else if (sendPlain)
                {
                    await sender.SendTokenStreamChunkAsync(package.InvocationId,
                        textChunk,
                        false,
                        sequence,
                        invocationToken).ConfigureAwait(false);
                }
            }

            if (sendEncrypted)
            {
                await sender.SendEncryptedCompletedAsync(_envelopeCryptoService.EncryptCompleted(package.ConversationId,
                        context.MessageId,
                        context.EpochVersion,
                        context.EpochKey.Span,
                        Encoding.UTF8.GetBytes(responseBuilder.ToString()),
                        sequence,
                        usageSnapshot?.ToTokenCounts() ?? new Dictionary<string, long>(),
                        reasoningBuilder.Length > 0 ? Encoding.UTF8.GetBytes(reasoningBuilder.ToString()) : null),
                    invocationToken).ConfigureAwait(false);
            }
            else if (sendPlain)
            {
                if (usageSnapshot is null)
                {
                    _logger.LogWarning("Terminal model usage was not reported for invocation {InvocationId} using model {ModelName}. Token fields will remain unknown.",
                        package.InvocationId,
                        resolvedModel);
                }

                await sender.SendReasoningStreamChunkAsync(package.InvocationId,
                    string.Empty,
                    true,
                    reasoningSequence + 1,
                    invocationToken).ConfigureAwait(false);
                await sender.SendTokenStreamChunkAsync(package.InvocationId,
                    string.Empty,
                    true,
                    sequence + 1,
                    invocationToken).ConfigureAwait(false);
                await sender.SendInvocationCompletedAsync(new InvocationCompletedPayload
                {
                    InvocationId = package.InvocationId,
                    FinalContent = responseBuilder.ToString(),
                    ModelUsed = resolvedModel,
                    InputTokens = usageSnapshot?.InputTokens,
                    OutputTokens = usageSnapshot?.OutputTokens,
                    TokensUsed = usageSnapshot?.TotalTokens,
                    FinalReasoning = reasoningBuilder.ToString(),
                    ReasoningTokens = usageSnapshot?.ReasoningTokens
                }, invocationToken).ConfigureAwait(false);
            }

            await dispatcher.ReportInvocationCompletedAsync(package.InvocationId,
                usageSnapshot?.InputTokens,
                usageSnapshot?.OutputTokens,
                usageSnapshot?.TotalTokens,
                usageSnapshot?.ReasoningTokens).ConfigureAwait(false);
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

    public Task<string> ExecuteApiToolCallAsync(Guid invocationId,
        string toolName,
        string parameters,
        CancellationToken cancellationToken = default)
    {
        // Default to the approval-gated path; the per-tool overload below is what BuildInvocationTools wires in,
        // passing the tool's RequiresApproval flag so non-approval tools auto-execute.
        return ExecuteApiToolCallAsync(invocationId, toolName, parameters, true, cancellationToken);
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

    private InvocationAgentDefinition BuildInvocationDefinition(Models.RuntimePackage package, string resolvedModel)
    {
        var messages = BuildChatMessages(package);

        return new InvocationAgentDefinition(resolvedModel,
            package.ResolvedSystemPrompt,
            BuildInvocationTools(package),
            messages,
            package.ReasoningEffort);
    }

    private static IReadOnlyList<ChatMessage> BuildChatMessages(Models.RuntimePackage package)
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

    private IReadOnlyList<AITool> BuildInvocationTools(Models.RuntimePackage package)
    {
        // The runtime package only carries the OFFER list. Api-side tools get a real bridge that round-trips to the
        // platform; client-local (catalog) tools get a name-only placeholder, and the invocation factory swaps it for
        // the matching executable from IAgentToolRegistry before the agent runs.
        return
        [
            .. package.AllowedTools.Select(tool => tool.Location switch
            {
                ToolLocation.ApiSide => InvocationToolBridge.Create(tool.Name,
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

    private static bool IsLocalLoopbackInvocation(Models.RuntimePackage package)
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

    public sealed class WorkerToolCallException : Exception
    {
        public WorkerToolCallException(string toolName, string message, Exception? innerException = null)
            : base($"Tool call '{toolName}' failed: {message}", innerException)
        {
        }
    }
}
