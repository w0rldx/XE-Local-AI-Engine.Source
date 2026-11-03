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
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
// Aliased so OpenAI.Chat.ChatMessage never collides with the MEAI ChatMessage this file speaks.
using ChatTokenLogProbabilityDetails = OpenAI.Chat.ChatTokenLogProbabilityDetails;

/// <summary>Sends <c>[System?, User]</c> straight to the node's <see cref="IChatClient" /> and relays what comes back.</summary>
/// <remarks>
///     The singleton client is the cloud/local router, so model routing and its egress checks are the chat path's own.
///     The request takes the node's invocation slot like a chat turn, but writes no conversation and no run row; the
///     terminal state it reports only closes the slot's live entry. Generation runs under the node's message deadline and,
///     for an installed llama.cpp model whose request sets no limit of its own, a default output cap. Token probabilities
///     are requested and read only when the request asks, and refused for any other model.
/// </remarks>
public sealed class DirectInferenceService : IDirectInferenceService
{
    /// <summary>The output cap for a llama.cpp request that sets none; thinking counts toward it. A test holds the Direct Inference page's copy to this value.</summary>
    internal const int DefaultMaxOutputTokens = 16_384;

    /// <summary>The most alternatives one request may ask for per entry. Upstream sets no limit, so this is ours (OpenAI's documented maximum). A test holds the page's copy to it.</summary>
    internal const int MaxTopLogprobs = 20;

    private readonly IChatClient _chatClient;
    private readonly IWorkerEventDispatcher _dispatcher;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly ILocalModelProviderResolver _localModelProviderResolver;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DirectInferenceService> _logger;

    public DirectInferenceService(IChatClient chatClient,
        IWorkerEventDispatcher dispatcher,
        IGpuWorkGate gpuWorkGate,
        INodeSettingsStore nodeSettingsStore,
        IGgufModelStore ggufModelStore,
        IModelTrustResolver modelTrustResolver,
        ILocalModelProviderResolver localModelProviderResolver,
        TimeProvider timeProvider,
        ILogger<DirectInferenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        ArgumentNullException.ThrowIfNull(nodeSettingsStore);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        ArgumentNullException.ThrowIfNull(localModelProviderResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _chatClient = chatClient;
        _dispatcher = dispatcher;
        _gpuWorkGate = gpuWorkGate;
        _nodeSettingsStore = nodeSettingsStore;
        _ggufModelStore = ggufModelStore;
        _modelTrustResolver = modelTrustResolver;
        _localModelProviderResolver = localModelProviderResolver;
        _timeProvider = timeProvider;
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
        return StreamCoreAsync(modelId, messages, options, additionalProperties, request.TopLogprobs, cancellationToken);
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

        if (request.TopLogprobs is < 1 or > MaxTopLogprobs)
        {
            errors.Add($"Token probabilities must be between 1 and {MaxTopLogprobs}.");
        }

        return errors.Count > 0 ? throw new DirectInferenceInvalidRequestException(string.Join(" ", errors)) : sampling;
    }

    private async IAsyncEnumerable<DirectInferenceEvent> StreamCoreAsync(string modelId,
        List<ChatMessage> messages,
        ChatOptions options,
        AdditionalPropertiesDictionary additionalProperties,
        int? topLogprobs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var invocationId = Guid.NewGuid();

        // Fail fast rather than queue silently behind a job that may hold the GPU for hours.
        if (GpuWorkHolderNotice.ForModelLoad(_gpuWorkGate, invocationId) is { } holderNotice)
        {
            yield return ErrorEvent(DirectInferenceErrorCodes.GpuWorkExclusive, holderNotice.Message);
            yield break;
        }

        // Read before admission: past it, a failed read would leave the live entry open.
        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);

        // An unknown id takes no default either: it is not installed, so the provider refuses it before generating anything.
        if (await NodeLocalModelGate.IsInstalledNodeLocalLlamaModelAsync(modelId, _ggufModelStore, _modelTrustResolver, _localModelProviderResolver, cancellationToken))
        {
            ReasoningOptionsResolver.ApplyDefaultOutputCap(options, additionalProperties, DefaultMaxOutputTokens);
            if (topLogprobs is { } count)
            {
                options = LlamaServerLogprobs.Request(options, count);
            }
        }
        else if (topLogprobs is not null)
        {
            yield return ErrorEvent(DirectInferenceErrorCodes.LogprobsLocalOnly, "Token probabilities are available for installed llama.cpp models only.");
            yield break;
        }

        await using var lease = await InvocationSlotAdmission.AcquireAsync(_dispatcher, AdmissionPackage(invocationId, modelId, messages), cancellationToken);

        // Started only now: time queued behind another turn is not generation time.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(nodeSettings.MaxMessageRequestTimeoutSeconds), _timeProvider);
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var terminalReported = false;
        try
        {
            var startedTimestamp = Stopwatch.GetTimestamp();
            long? firstTokenMs = null;
            string? finishReason = null;
            UsageDetails? usage = null;

            var updates = Defer(() => _chatClient.GetStreamingResponseAsync(messages, options, generation.Token), generation.Token).GetAsyncEnumerator(generation.Token);
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
                    // The consumer's own token keeps its propagation: a Stop stays Cancelled even if the deadline fired beside it.
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(exception, "Direct inference {InvocationId} failed.", invocationId);
                        // Past the deadline, whatever the provider threw is how its cancelled request surfaced.
                        failure = await ReportFailureAsync(invocationId, deadline.IsCancellationRequested ? new TimeoutException() : exception);
                        terminalReported = true;
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

                    if (topLogprobs is not null && LlamaServerLogprobs.Read(update.RawRepresentation) is { Count: > 0 } entries)
                    {
                        yield return new DirectInferenceEvent
                        {
                            Type = DirectInferenceEventTypes.Logprobs,
                            Logprobs = ToLogprobEntries(entries, isReasoning: reasoning is { Length: > 0 })
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

            // A provider may answer its cancelled token by simply ending the enumeration: that is a timeout, not a finished answer.
            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Direct inference {InvocationId} reached its deadline.", invocationId);
                var timedOut = await ReportFailureAsync(invocationId, new TimeoutException());
                terminalReported = true;
                yield return timedOut;
                yield break;
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

    private async Task<DirectInferenceEvent> ReportFailureAsync(Guid invocationId, Exception exception)
    {
        var (category, message) = InvocationFailureClassifier.MapFailure(exception);
        await _dispatcher.ReportInvocationFailedAsync(invocationId, message, category);
        return ErrorEvent(category.ToString(), message);
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
            // Never run by the invocation runner, so neither is read: this service holds the deadline, the hash names no agent.
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

    private static DirectInferenceLogprobEntry[] ToLogprobEntries(IReadOnlyList<ChatTokenLogProbabilityDetails> entries, bool isReasoning) =>
    [
        .. entries.Select(entry => new DirectInferenceLogprobEntry
        {
            Text = entry.Token,
            Logprob = entry.LogProbability,
            IsReasoning = isReasoning,
            Alternatives =
            [
                .. entry.TopLogProbabilities.Select(static alternative => new DirectInferenceLogprobAlternative
                {
                    Text = alternative.Token,
                    Logprob = alternative.LogProbability
                })
            ]
        })
    ];

    private static long ElapsedMs(long startedTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    private static int? ToInt(long? value) =>
        value is { } resolved ? (int)Math.Clamp(resolved, 0, int.MaxValue) : null;
}
