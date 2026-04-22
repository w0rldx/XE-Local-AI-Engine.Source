namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Collections.Concurrent;
using System.Text;
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
    private const string ProviderUnavailableMessage = "Provider unreachable.";

    private static readonly Regex FrameworkExceptionNamePattern =
        new(@"\b(?:Microsoft|System)(?:\.[A-Za-z_][A-Za-z0-9_]*)*\.[A-Za-z_][A-Za-z0-9_]*Exception\b|\b(?:AgentException|ChatClientAgentException)\b", RegexOptions.CultureInvariant);

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

    public async Task RunAsync(RuntimePackage package, CancellationToken cancellationToken = default)
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

        RegisterActiveInvocation(package.InvocationId, package.Timeouts.InvocationTimeoutSeconds, cancellationToken);

        try
        {
            var invocationToken = GetInvocationCancellationToken();
            var resolvedModel = await ResolveModelAsync(package.ModelProfile, invocationToken).ConfigureAwait(false);
            var responseBuilder = new StringBuilder();
            var streamedChunkCount = 0;
            long sequence = 0;
            var totalResponseBytes = 0;

            if (shouldSendHubMessages)
            {
                await sender.SendInvocationAcceptedAsync(package.InvocationId, invocationToken).ConfigureAwait(false);
            }

            var definition = BuildInvocationDefinition(package, resolvedModel);
            await using var agentContext = await _invocationAgentFactory.CreateAsync(definition, invocationToken).ConfigureAwait(false);

            await foreach (var update in agentContext.Agent.RunStreamingAsync(agentContext.SeedMessages, null, agentContext.RunOptions, invocationToken).ConfigureAwait(false))
            {
                var textChunk = update.Text;
                var thinkingChunk = string.Concat(update.Contents?.OfType<TextReasoningContent>()
                                                        .Select(t => t.Text) ?? Enumerable.Empty<string>());

                if (!string.IsNullOrEmpty(thinkingChunk))
                {
                    await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, thinkingChunk).ConfigureAwait(false);
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

                if (shouldSendHubMessages)
                {
                    await sender.SendEncryptedChunkAsync(_envelopeCryptoService.EncryptChunk(package.ConversationId,
                            context.MessageId,
                            context.EpochVersion,
                            context.EpochKey.Span,
                            Encoding.UTF8.GetBytes(textChunk),
                            sequence),
                        invocationToken).ConfigureAwait(false);
                }
            }

            if (shouldSendHubMessages)
            {
                await sender.SendEncryptedCompletedAsync(_envelopeCryptoService.EncryptCompleted(package.ConversationId,
                        context.MessageId,
                        context.EpochVersion,
                        context.EpochKey.Span,
                        Encoding.UTF8.GetBytes(responseBuilder.ToString()),
                        sequence,
                        new Dictionary<string, long>
                        {
                            ["tokensUsed"] = streamedChunkCount
                        }),
                    invocationToken).ConfigureAwait(false);
            }

            await dispatcher.ReportInvocationCompletedAsync(package.InvocationId).ConfigureAwait(false);
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
            ClearActiveInvocation(package.InvocationId);
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

    public async Task<string> ExecuteApiToolCallAsync(Guid invocationId,
        string toolName,
        string parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(parameters);

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ToolCallResultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingToolCall = new PendingToolCall(invocationId, DateTimeOffset.UtcNow, completion);
        var sender = _hubSender.Value;

        if (!_pendingToolCalls.TryAdd(requestId, pendingToolCall))
        {
            throw new InvalidOperationException("Failed to register pending tool call.");
        }

        try
        {
            var payload = new ToolCallRequestPayload
            {
                InvocationId = invocationId,
                RequestId = requestId,
                ToolName = toolName,
                Parameters = parameters
            };

            await sender.SendToolCallRequestAsync(payload,
                cancellationToken).ConfigureAwait(false);
            await _eventDispatcher.Value.ReportToolCallRequestedAsync(payload).ConfigureAwait(false);

            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

            var result = await completion.Task.WaitAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                throw new WorkerToolCallException(toolName, result.Error);
            }

            return result.Result;
        }
        catch (TimeoutException timeoutException)
        {
            throw new WorkerToolCallException(toolName, timeoutException.Message, timeoutException);
        }
        catch (OperationCanceledException operationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkerToolCallException(toolName, "Tool call timed out waiting for a result.", operationCanceledException);
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
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
                removedPendingToolCall.Completion.TrySetCanceled();
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
                removedPendingToolCall.Completion.TrySetException(new TimeoutException("Tool call timed out during cleanup."));
            }
        }
    }

    public void ResolveToolCallResult(ToolCallResultEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_pendingToolCalls.TryRemove(evt.RequestId, out var pendingToolCall))
        {
            pendingToolCall.Completion.TrySetResult(evt);
        }
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
            messages);
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
        return package.AllowedTools
                      .Where(static tool => tool.Location == ToolLocation.ApiSide)
                      .Select(tool => InvocationToolBridge.Create(tool.Name,
                          (arguments, cancellationToken) => ExecuteApiToolCallAsync(package.InvocationId, tool.Name, arguments, cancellationToken)))
                      .ToList();
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
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report invocation failure to the API for {InvocationId}. Enqueueing to dead letter store.", context.Package.InvocationId);
            await _deadLetterStore.EnqueueAsync(new InvocationFailedPayload
            {
                InvocationId = context.Package.InvocationId,
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
                removedPendingToolCall.Completion.TrySetCanceled();
            }
        }
    }

    private void RegisterActiveInvocation(Guid invocationId, int timeoutSeconds, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_currentInvocationId is not null)
            {
                throw new InvalidOperationException("Worker is busy with another invocation");
            }

            _currentInvocationId = invocationId;
            _userCancelRequested = false;
            _timeoutTriggered = false;
            _invocationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _invocationCancellationTokenSource.Token.Register(() =>
            {
                lock (_syncRoot)
                {
                    if (!_userCancelRequested)
                    {
                        _timeoutTriggered = true;
                    }
                }
            });
            _invocationCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
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

    private sealed record PendingToolCall(
        Guid InvocationId,
        DateTimeOffset CreatedAt,
        TaskCompletionSource<ToolCallResultEvent> Completion);

    public sealed class WorkerToolCallException : Exception
    {
        public WorkerToolCallException(string toolName, string message, Exception? innerException = null)
            : base($"Tool call '{toolName}' failed: {message}", innerException)
        {
        }
    }
}
