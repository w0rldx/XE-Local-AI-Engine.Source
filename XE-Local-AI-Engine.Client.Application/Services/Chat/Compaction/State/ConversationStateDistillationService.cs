namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>Brings a conversation's distilled state up to date with its completed messages on a node-local model.</summary>
public interface IConversationStateDistillationService
{
    /// <param name="upToAnchorSequence">Distil only messages at or below this anchor; null distils everything pending.</param>
    Task<ConversationStateDistillationOutcome> DistillPendingAsync(Guid conversationId,
        string? requestedModel,
        int? upToAnchorSequence,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Default <see cref="IConversationStateDistillationService" />: calls the distiller in a loop over the messages
///     after the state watermark and persists state plus watermark after EVERY call, under one shared deadline.
/// </summary>
internal sealed class ConversationStateDistillationService : IConversationStateDistillationService
{
    private readonly INodeChatPersistenceService _persistence;
    private readonly IConversationStateDistiller _distiller;
    private readonly ILocalDefaultChatModelResolver _localDefaultChatModelResolver;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly LocalRuntimeWarmer _localRuntimeWarmer;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly ConversationCompactionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationStateDistillationService> _logger;

    public ConversationStateDistillationService(INodeChatPersistenceService persistence,
        IConversationStateDistiller distiller,
        ILocalDefaultChatModelResolver localDefaultChatModelResolver,
        IModelCapabilityResolver modelCapabilityResolver,
        LocalRuntimeWarmer localRuntimeWarmer,
        INodeSettingsStore nodeSettingsStore,
        IOptions<ConversationCompactionOptions> options,
        TimeProvider timeProvider,
        ILogger<ConversationStateDistillationService> logger)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _distiller = distiller ?? throw new ArgumentNullException(nameof(distiller));
        _localDefaultChatModelResolver = localDefaultChatModelResolver ?? throw new ArgumentNullException(nameof(localDefaultChatModelResolver));
        _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
        _localRuntimeWarmer = localRuntimeWarmer ?? throw new ArgumentNullException(nameof(localRuntimeWarmer));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     The completed, content-bearing messages of the selected path after the state watermark (and at or below
    ///     <paramref name="upToAnchorSequence" />), in anchor order: the span compaction folds, seen from the state side.
    /// </summary>
    internal static List<(NodeChatPersistedMessageDto Message, int Anchor)> PendingMessages(NodeChatConversationDto conversation, int? upToAnchorSequence)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var watermark = conversation.ConversationStateCoversToSequence;
        return SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath)
                                   .Where(static message => !string.IsNullOrWhiteSpace(message.Content)
                                                            && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                                   .Select(message => (Message: message, Anchor: anchorSequence(message)))
                                   .Where(pair => (watermark is null || pair.Anchor > watermark.Value) && (upToAnchorSequence is null || pair.Anchor <= upToAnchorSequence.Value))
                                   .OrderBy(static pair => pair.Anchor)
                                   .ToList();
    }

    public async Task<ConversationStateDistillationOutcome> DistillPendingAsync(Guid conversationId,
        string? requestedModel,
        int? upToAnchorSequence,
        CancellationToken cancellationToken = default)
    {
        if (!_options.DistillEnabled)
        {
            return new ConversationStateDistillationOutcome
            {
                Status = ConversationStateDistillationStatus.Disabled
            };
        }

        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);
        // One budget for resolution and ALL calls, the CompactAsync pattern; persisted calls survive its expiry.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(nodeSettings.MaxMessageRequestTimeoutSeconds), _timeProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var progress = new Progress();
        try
        {
            return await DistillCoreAsync(conversationId, requestedModel, upToAnchorSequence, nodeSettings, progress, operation.Token, cancellationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Distillation timed out for conversation {ConversationId} after {Calls} persisted call(s).", conversationId, progress.Calls);
            return new ConversationStateDistillationOutcome
            {
                Status = ConversationStateDistillationStatus.TimedOut,
                Calls = progress.Calls,
                CoversToSequence = progress.CoversToSequence,
                Document = progress.Document
            };
        }
    }

    private async Task<ConversationStateDistillationOutcome> DistillCoreAsync(Guid conversationId,
        string? requestedModel,
        int? upToAnchorSequence,
        StoredNodeSettings nodeSettings,
        Progress progress,
        CancellationToken operationToken,
        CancellationToken callerToken)
    {
        var conversation = await _persistence.GetConversationAsync(conversationId, operationToken);
        if (conversation is null)
        {
            return new ConversationStateDistillationOutcome
            {
                Status = ConversationStateDistillationStatus.ConversationNotFound
            };
        }

        // Null or corrupt stored JSON starts a fresh document; the watermark still bounds what is pending.
        progress.Document = ConversationStateSerializer.Deserialize(conversation.ConversationState);
        progress.CoversToSequence = conversation.ConversationStateCoversToSequence;

        var sources = PendingMessages(conversation, upToAnchorSequence)
                      .Select(static pair => ConversationStateSourceMessageMapper.Map(pair.Message, pair.Anchor))
                      .ToList();
        if (sources.Count == 0)
        {
            return progress.ToOutcome(ConversationStateDistillationStatus.NothingToDistill);
        }

        // Node-local only, resolved exactly as compaction resolves its fold model.
        var preferred = string.IsNullOrWhiteSpace(requestedModel) ? nodeSettings.DefaultModelName : requestedModel;
        var model = await _localDefaultChatModelResolver.ResolveAsync(preferred, operationToken);
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogInformation("Distillation skipped for conversation {ConversationId}: no installed local chat model.", conversationId);
            return progress.ToOutcome(ConversationStateDistillationStatus.NoLocalModel);
        }

        var capabilities = await _modelCapabilityResolver.ResolveAsync(model, operationToken);
        var warmProvider = await _localRuntimeWarmer.ResolveWarmableProviderAsync(model, conversationId, operationToken);
        var effectiveContextTokens = warmProvider is null
            ? null
            : await _localRuntimeWarmer.ResolveEffectiveContextTokensAsync(warmProvider, model, conversationId, operationToken);

        var document = progress.Document ?? new ConversationStateDocument();
        // The stamp the row carried when read (a clear restamps it): every write is guarded on it, so a delta distilled
        // from a path the user has since left is discarded instead of restoring state over the clear.
        var expectedStamp = conversation.ConversationStateUpdatedAtUtc;
        while (sources.Count > 0)
        {
            var distillation = await _distiller.DistillAsync(new ConversationStateDistillerInput
                {
                    State = document,
                    Messages = sources,
                    ModelName = model,
                    SupportsThinking = capabilities.SupportsThinking,
                    EffectiveContextTokens = effectiveContextTokens
                },
                operationToken);
            // A provider may finish concurrently with the deadline; never persist after it.
            operationToken.ThrowIfCancellationRequested();
            if (distillation is null)
            {
                break;
            }

            var reduced = ConversationStateReducer.Apply(document, distillation.Delta, distillation.CoversToSequence, _options);
            foreach (var rejection in reduced.Rejections)
            {
                _logger.LogDebug("Distillation for conversation {ConversationId} rejected {Operation} {EntryId}: {Reason}.",
                    conversationId, rejection.Operation, rejection.EntryId, rejection.Reason);
            }

            if (reduced.DroppedIds.Count > 0)
            {
                _logger.LogDebug("Distillation for conversation {ConversationId} dropped {Count} entry(ies) over budget.", conversationId, reduced.DroppedIds.Count);
            }

            document = reduced.Document;
            var stamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            // The caller's token, not the deadline: a finished call's write must not be torn by the budget.
            var written = await _persistence.SetConversationStateAsync(new NodeChatSetConversationStateRequest
                {
                    ConversationId = conversationId,
                    State = ConversationStateSerializer.Serialize(document),
                    CoversToSequence = distillation.CoversToSequence,
                    UpdatedAtUtc = stamp,
                    GuardUnchanged = true,
                    ExpectedUpdatedAtUtc = expectedStamp
                },
                callerToken);
            if (written is null)
            {
                _logger.LogInformation("Distillation for conversation {ConversationId} was superseded by a path change after {Calls} persisted call(s); the last delta was discarded.",
                    conversationId, progress.Calls);
                return progress.ToOutcome(ConversationStateDistillationStatus.Superseded);
            }

            expectedStamp = stamp;
            progress.Calls++;
            progress.CoversToSequence = distillation.CoversToSequence;
            progress.Document = document;
            sources.RemoveRange(0, Math.Clamp(distillation.MessagesConsumed, 1, sources.Count));
        }

        return progress.ToOutcome(progress.Calls == 0 ? ConversationStateDistillationStatus.DistillerReturnedNothing : ConversationStateDistillationStatus.Distilled);
    }

    private sealed class Progress
    {
        public int Calls { get; set; }

        public int? CoversToSequence { get; set; }

        public ConversationStateDocument? Document { get; set; }

        public ConversationStateDistillationOutcome ToOutcome(ConversationStateDistillationStatus status) =>
            new()
            {
                Status = status,
                Calls = Calls,
                CoversToSequence = CoversToSequence,
                Document = Document
            };
    }
}
