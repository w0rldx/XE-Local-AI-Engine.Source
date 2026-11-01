namespace XE_Local_AI_Engine.Client.Services.Inference.Implementation;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Sends <c>[System?, User]</c> straight to the node's <see cref="IChatClient" /> and relays what comes back.</summary>
/// <remarks>
///     The singleton client is the cloud/local router, so model routing and its egress checks are the chat path's own.
///     The request takes the node's invocation slot like a chat turn, but writes no conversation and no run row; the
///     terminal state it reports only closes the slot's live entry.
/// </remarks>
public sealed class DirectInferenceService : IDirectInferenceService
{
    private readonly IChatClient _chatClient;
    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly ILogger<DirectInferenceService> _logger;

    public DirectInferenceService(IChatClient chatClient,
        IWorkerEventDispatcher dispatcher,
        IGpuWorkGate gpuWorkGate,
        ILogger<DirectInferenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        ArgumentNullException.ThrowIfNull(logger);
        _chatClient = chatClient;
        _dispatcher = dispatcher;
        _gpuWorkGate = gpuWorkGate;
        _logger = logger;
    }

    public IAsyncEnumerable<DirectInferenceEvent> StreamAsync(DirectInferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Eager, so the hub can turn a bad request into a HubException before any stream exists.
        var sampling = ValidateAndMapSampling(request);
        var modelId = request.ModelId!.Trim();
        List<ChatMessage> messages = [];
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
        }

        messages.Add(new ChatMessage(ChatRole.User, request.Prompt));
        AdditionalPropertiesDictionary additionalProperties = [];
        var options = new ChatOptions
        {
            ModelId = modelId,
            Tools = null,
            AdditionalProperties = additionalProperties
        };
        InvocationSamplingRules.Apply(options, additionalProperties, sampling);
        return StreamCoreAsync(modelId, messages, options, cancellationToken);
    }

    private static InvocationSamplingOptions? ValidateAndMapSampling(DirectInferenceRequest request)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            errors.Add("A model is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            errors.Add("A prompt is required.");
        }

        if (!SeedValue.TryParse(request.SamplingOptions?.Seed, out _, out var seedError))
        {
            errors.Add(seedError);
        }

        var sampling = InvocationRunner.MapSamplingOptions(request.SamplingOptions);
        if (sampling is not null)
        {
            errors.AddRange(InvocationSamplingRules.Validate(sampling));
        }

        return errors.Count > 0 ? throw new DirectInferenceInvalidRequestException(string.Join(" ", errors)) : sampling;
    }

    private async IAsyncEnumerable<DirectInferenceEvent> StreamCoreAsync(string modelId,
        List<ChatMessage> messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var invocationId = Guid.NewGuid();

        // Fail fast rather than queue silently behind a job that may hold the GPU for hours.
        if (GpuWorkHolderNotice.ForModelLoad(_gpuWorkGate, invocationId) is { } holderNotice)
        {
            yield return ErrorEvent(DirectInferenceErrorCodes.GpuWorkExclusive, holderNotice.Message);
            yield break;
        }

        await using var lease = await InvocationSlotAdmission.AcquireAsync(_dispatcher, AdmissionPackage(invocationId, modelId, messages), cancellationToken);
        var terminalReported = false;
        try
        {
            var startedTimestamp = Stopwatch.GetTimestamp();
            long? firstTokenMs = null;
            string? finishReason = null;
            UsageDetails? usage = null;

            var updates = Defer(() => _chatClient.GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (updates)
            {
                while (true)
                {
                    DirectInferenceEvent? failure = null;
                    try
                    {
                        if (!await updates.MoveNextAsync())
                        {
                            break;
                        }
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(exception, "Direct inference {InvocationId} failed.", invocationId);
                        var (category, message) = InvocationFailureClassifier.MapFailure(exception);
                        await _dispatcher.ReportInvocationFailedAsync(invocationId, message, category);
                        terminalReported = true;
                        failure = ErrorEvent(category.ToString(), message);
                    }

                    if (failure is not null)
                    {
                        yield return failure;
                        yield break;
                    }

                    var update = updates.Current;
                    if (update.FinishReason is { } reason && !string.IsNullOrEmpty(reason.Value))
                    {
                        finishReason = reason.Value;
                    }

                    StringBuilder? reasoning = null;
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case TextReasoningContent reasoningContent:
                                (reasoning ??= new StringBuilder()).Append(reasoningContent.Text);
                                break;

                            case UsageContent usageContent:
                                usage = usageContent.Details;
                                yield return new DirectInferenceEvent
                                {
                                    Type = DirectInferenceEventTypes.Usage,
                                    Usage = ToUsage(usageContent.Details)
                                };
                                break;
                        }
                    }

                    var text = update.Text;
                    if (!string.IsNullOrEmpty(text) || reasoning is { Length: > 0 })
                    {
                        firstTokenMs ??= ElapsedMs(startedTimestamp);

                        // The same per-chunk reports a chat turn makes: they drive the live state and the completion log's chunk counts.
                        if (reasoning is { Length: > 0 })
                        {
                            await _dispatcher.ReportInvocationThinkingChunkAsync(invocationId, reasoning.ToString());
                        }

                        if (!string.IsNullOrEmpty(text))
                        {
                            await _dispatcher.ReportInvocationStreamChunkAsync(invocationId, text);
                        }

                        yield return new DirectInferenceEvent
                        {
                            Type = DirectInferenceEventTypes.Delta,
                            TextDelta = string.IsNullOrEmpty(text) ? null : text,
                            ReasoningDelta = reasoning is { Length: > 0 } ? reasoning.ToString() : null
                        };
                    }

                    if (LlamaServerGenerationTimings.ReadOrNull(update.RawRepresentation) is { } timings)
                    {
                        yield return new DirectInferenceEvent
                        {
                            Type = DirectInferenceEventTypes.Timings,
                            Timings = new DirectInferenceTimings
                            {
                                PromptTokens = timings.PromptTokens,
                                PromptMs = timings.PromptMs,
                                GenerationTokens = timings.GenerationTokens,
                                GenerationMs = timings.GenerationMs,
                                CachedPromptTokens = timings.CachedPromptTokens
                            }
                        };
                    }
                }
            }

            var elapsedMs = ElapsedMs(startedTimestamp);
            await _dispatcher.ReportInvocationCompletedAsync(invocationId,
                ToInt(usage?.InputTokenCount),
                ToInt(usage?.OutputTokenCount),
                ToInt(usage?.TotalTokenCount),
                ToInt(usage?.ReasoningTokenCount),
                elapsedMs,
                finishReason);
            terminalReported = true;
            yield return new DirectInferenceEvent
            {
                Type = DirectInferenceEventTypes.Done,
                ElapsedMs = elapsedMs,
                FirstTokenMs = firstTokenMs,
                FinishReason = finishReason
            };
        }
        finally
        {
            // The consumer cancelled or walked away: close the live entry so nothing reads this request as still running.
            if (!terminalReported)
            {
                await _dispatcher.ReportInvocationFailedAsync(invocationId, "The request was cancelled.", FailureCategory.Cancelled);
            }
        }
    }

    /// <summary>Moves a synchronous throw out of <see cref="IChatClient.GetStreamingResponseAsync" /> (a refused egress) into the first <c>MoveNextAsync</c>.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> Defer(Func<IAsyncEnumerable<ChatResponseUpdate>> start,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in start().WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>The slot's live entry: no conversation, no agent, no tools; the model is what the invocation monitor shows.</summary>
    private static RuntimePackage AdmissionPackage(Guid invocationId, string modelId, List<ChatMessage> messages) =>
        new()
        {
            InvocationId = invocationId,
            ConversationId = Guid.Empty,
            ClientNodeId = LocalChatLoopbackDefaults.ClientNodeId,
            AgentDefinitionVersion = 0,
            ResolvedSystemPrompt = messages[0].Role == ChatRole.System ? messages[0].Text : string.Empty,
            OmitSystemPrompt = messages[0].Role != ChatRole.System,
            ConversationContext = [],
            AllowedTools = [],
            ModelProfile = modelId,
            // Never run by the invocation runner, so neither is read: the timeout is the stream token, the hash names no agent.
            Timeouts = new TimeoutSettings(),
            ConfigHash = string.Empty
        };

    private static DirectInferenceEvent ErrorEvent(string code, string message) =>
        new()
        {
            Type = DirectInferenceEventTypes.Error,
            ErrorCode = code,
            ErrorMessage = message
        };

    private static DirectInferenceUsage ToUsage(UsageDetails details) =>
        new()
        {
            InputTokens = details.InputTokenCount,
            OutputTokens = details.OutputTokenCount,
            TotalTokens = details.TotalTokenCount,
            ReasoningTokens = details.ReasoningTokenCount,
            CachedInputTokens = details.CachedInputTokenCount
        };

    private static long ElapsedMs(long startedTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    private static int? ToInt(long? value) =>
        value is { } resolved ? (int)Math.Clamp(resolved, 0, int.MaxValue) : null;
}
