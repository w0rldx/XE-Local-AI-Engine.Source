namespace XE_Local_AI_Engine.Client.Services.Invocation;

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;

public sealed class InvocationRunner : IInvocationRunner
{
    private readonly ICapabilityReporter _capabilityReporter;
    private readonly IChatClient _chatClient;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly string _defaultModel;
    private readonly Lazy<IHubMessageSender> _hubSender;
    private readonly ILogger<InvocationRunner> _logger;
    private readonly TimeSpan _maxPendingToolCallAge;
    private readonly int _maxResponseSizeBytes;

    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls = new(StringComparer.Ordinal);
    private readonly IRuntimePackageValidator _runtimePackageValidator;
    private readonly object _syncRoot = new();
    private Guid? _currentInvocationId;

    private CancellationTokenSource? _invocationCancellationTokenSource;

    public InvocationRunner(Lazy<IHubMessageSender> hubSender,
        IChatClient chatClient,
        IRuntimePackageValidator runtimePackageValidator,
        ICapabilityReporter capabilityReporter,
        IDeadLetterStore deadLetterStore,
        IConfiguration configuration,
        IOptions<WorkerNodeOptions> workerOptions,
        ILogger<InvocationRunner> logger)
    {
        _hubSender = hubSender ?? throw new ArgumentNullException(nameof(hubSender));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _runtimePackageValidator = runtimePackageValidator ?? throw new ArgumentNullException(nameof(runtimePackageValidator));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _deadLetterStore = deadLetterStore ?? throw new ArgumentNullException(nameof(deadLetterStore));
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(workerOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _defaultModel = configuration.GetValue<string>("Ollama:ChatModel")
                        ?? throw new InvalidOperationException("Ollama:ChatModel is required for invocation execution.");
        _maxResponseSizeBytes = workerOptions.Value.MaxResponseSizeMb * 1024 * 1024;
        _maxPendingToolCallAge = TimeSpan.FromMinutes(workerOptions.Value.MaxPendingToolCallAgeMinutes);
    }

    public async Task RunAsync(RuntimePackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var validationResult = _runtimePackageValidator.Validate(package);
        if (!validationResult.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validationResult.Errors));
        }

        var sender = _hubSender.Value;

        RegisterActiveInvocation(package.InvocationId, package.Timeouts.InvocationTimeoutSeconds, cancellationToken);

        try
        {
            var invocationToken = GetInvocationCancellationToken();
            var resolvedModel = await ResolveModelAsync(package.ModelProfile, invocationToken).ConfigureAwait(false);
            var messages = BuildChatMessages(package);
            var responseBuilder = new StringBuilder();
            var streamedChunkCount = 0;
            var totalResponseBytes = 0;

            await sender.SendInvocationAcceptedAsync(package.InvocationId, invocationToken).ConfigureAwait(false);

            var chatOptions = new ChatOptions
            {
                ModelId = resolvedModel
            };

            await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, chatOptions, invocationToken).ConfigureAwait(false))
            {
                var chunk = update.Text;
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                streamedChunkCount++;
                totalResponseBytes += Encoding.UTF8.GetByteCount(chunk);

                if (totalResponseBytes > _maxResponseSizeBytes)
                {
                    throw new InvalidOperationException($"Response size exceeded maximum of {_maxResponseSizeBytes / (1024 * 1024)}MB");
                }

                responseBuilder.Append(chunk);

                await sender.SendTokenStreamChunkAsync(package.InvocationId, chunk, false, invocationToken).ConfigureAwait(false);
            }

            await sender.SendTokenStreamChunkAsync(package.InvocationId, string.Empty, true, invocationToken).ConfigureAwait(false);
            await sender.SendInvocationCompletedAsync(new InvocationCompletedPayload
                {
                    InvocationId = package.InvocationId,
                    FinalContent = responseBuilder.ToString(),
                    ModelUsed = resolvedModel,
                    TokensUsed = streamedChunkCount
                },
                invocationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsCurrentInvocation(package.InvocationId))
        {
            await TrySendFailureAsync(sender, package.InvocationId, "Invocation timed out or was cancelled").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Invocation {InvocationId} failed.", package.InvocationId);
            await TrySendFailureAsync(sender, package.InvocationId, exception.Message).ConfigureAwait(false);
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
            }
        }

        invocationCancellationTokenSource?.Cancel();
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
        var pendingToolCall = new PendingToolCall(DateTimeOffset.UtcNow, completion);
        var sender = _hubSender.Value;

        if (!_pendingToolCalls.TryAdd(requestId, pendingToolCall))
        {
            throw new InvalidOperationException("Failed to register pending tool call.");
        }

        try
        {
            await sender.SendToolCallRequestAsync(new ToolCallRequestPayload
                {
                    InvocationId = invocationId,
                    RequestId = requestId,
                    ToolName = toolName,
                    Parameters = parameters
                },
                cancellationToken).ConfigureAwait(false);

            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

            var result = await completion.Task.WaitAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                throw new InvalidOperationException($"Tool call '{toolName}' failed: {result.Error}");
            }

            return result.Result;
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

    private static IReadOnlyList<ChatMessage> BuildChatMessages(RuntimePackage package)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, package.ResolvedSystemPrompt)
        };

        messages.AddRange(package.ConversationContext
                                 .OrderBy(message => message.SortOrder)
                                 .Select(CreateChatMessage));

        return messages;
    }

    private static ChatMessage CreateChatMessage(ConversationMessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ChatMessage(MapRole(message.Role), message.Content);
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

    private async Task TrySendFailureAsync(IHubMessageSender sender, Guid invocationId, string error)
    {
        try
        {
            await sender.SendInvocationFailedAsync(new InvocationFailedPayload
                {
                    InvocationId = invocationId,
                    Error = error
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report invocation failure to the API for {InvocationId}. Enqueueing to dead letter store.", invocationId);
            await _deadLetterStore.EnqueueAsync(new InvocationFailedPayload
            {
                InvocationId = invocationId,
                Error = error
            }, CancellationToken.None).ConfigureAwait(false);
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
            _invocationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
        }

        invocationCancellationTokenSource?.Dispose();
    }

    private sealed record PendingToolCall(
        DateTimeOffset CreatedAt,
        TaskCompletionSource<ToolCallResultEvent> Completion);
}
