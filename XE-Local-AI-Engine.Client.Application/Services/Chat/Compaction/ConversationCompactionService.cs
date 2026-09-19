namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IConversationCompactionService" />. Loads the conversation, picks the older span that is not
///     already covered by the existing synopsis and sits before the recent-keep window, summarizes it on-node, and
///     persists the (extended) synopsis via <see cref="INodeChatPersistenceService.SetCompactionSummaryAsync" />. The
///     original messages are untouched.
/// </summary>
internal sealed class ConversationCompactionService : IConversationCompactionService
{
    private readonly INodeChatPersistenceService _persistence;
    private readonly IConversationSummarizer _summarizer;
    private readonly ILocalDefaultChatModelResolver _localDefaultChatModelResolver;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly ConversationCompactionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationCompactionService> _logger;

    public ConversationCompactionService(
        INodeChatPersistenceService persistence,
        IConversationSummarizer summarizer,
        ILocalDefaultChatModelResolver localDefaultChatModelResolver,
        IModelCapabilityResolver modelCapabilityResolver,
        INodeSettingsStore nodeSettingsStore,
        IOptions<ConversationCompactionOptions> options,
        TimeProvider timeProvider,
        ILogger<ConversationCompactionService> logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
        ArgumentNullException.ThrowIfNull(summarizer);
        _summarizer = summarizer;
        ArgumentNullException.ThrowIfNull(localDefaultChatModelResolver);
        _localDefaultChatModelResolver = localDefaultChatModelResolver;
        ArgumentNullException.ThrowIfNull(modelCapabilityResolver);
        _modelCapabilityResolver = modelCapabilityResolver;
        ArgumentNullException.ThrowIfNull(nodeSettingsStore);
        _nodeSettingsStore = nodeSettingsStore;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<ConversationCompactionResult> CompactAsync(Guid conversationId, string? requestedModel = null, CancellationToken cancellationToken = default) =>
        CompactAsync(conversationId, requestedModel, recentMessagesToKeepVerbatim: null, cancellationToken);

    public async Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
        string? requestedModel,
        int? recentMessagesToKeepVerbatim,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _persistence.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return new ConversationCompactionResult { Outcome = ConversationCompactionOutcome.ConversationNotFound };
        }

        // Collapse regenerated variant siblings to the SELECTED path FIRST — exactly what the send path does via
        // SelectedPathResolver — so compaction folds only the messages the user actually chose. Without this, rejected
        // sibling answers would be merged into the synopsis while the send path later drops the selected messages through
        // the covered sequence, feeding future turns content the user never picked.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath);

        // Order and fold in ANCHOR space (each group's earliest member sequence), exactly as the send/regenerate paths
        // build their context. A sibling minted by regenerating an EARLY turn after later turns exist carries a raw
        // sequence past them, so raw-sequence ordering would treat that stale answer as the newest message: it would
        // survive the keep-verbatim window while a genuinely recent exchange got folded away instead. See
        // SelectedPathResolver.CreateAnchorResolver. The cutoff persisted below as CompactionSummaryCoversToSequence is
        // therefore an anchor too — with no variants anchor == raw sequence, so values written before this change stay
        // valid, and the send/regenerate paths compare against it in the same space.
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);

        // Only completed, content-bearing messages are sendable history — the same filter the send path applies before
        // budgeting — so they are the only messages worth folding into a synopsis.
        var completed = selected
                        .Where(static message => !string.IsNullOrWhiteSpace(message.Content)
                                                 && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                        .OrderBy(anchorSequence)
                        .ToList();

        // The per-call override wins when present, otherwise the configured window. The floor of 2 applies to both, so a
        // caller can shrink the window but never below the last exchange.
        var keep = Math.Max(2, recentMessagesToKeepVerbatim ?? _options.RecentMessagesToKeepVerbatim);
        if (completed.Count <= keep)
        {
            return new ConversationCompactionResult { Outcome = ConversationCompactionOutcome.NothingToCompact };
        }

        // Everything before the recent-keep window is foldable; the newest kept message is the first one we DON'T fold.
        var cutoffSequence = anchorSequence(completed[completed.Count - keep - 1]);
        var priorCover = conversation.CompactionSummaryCoversToSequence;
        var toFold = completed
                     .Where(message => anchorSequence(message) <= cutoffSequence && (priorCover is null || anchorSequence(message) > priorCover.Value))
                     .Select(static message => new ConversationSummarizerMessage { Role = message.Role, Content = message.Content })
                     .ToList();

        if (toFold.Count == 0)
        {
            // The synopsis already covers everything up to the cutoff — nothing new to add.
            return new ConversationCompactionResult
            {
                Outcome = ConversationCompactionOutcome.NothingToCompact,
                Summary = conversation.CompactionSummary,
                CoversToSequence = priorCover,
                MessagesFolded = 0,
                UpdatedAtUtc = conversation.CompactionSummaryUpdatedAtUtc
            };
        }

        // Summarize with the model the user is chatting with when it is an installed LOCAL chat model. The resolver
        // returns the requested model iff it is a local GGUF chat model, otherwise it falls back to an installed local
        // default — so a cloud selection (or an unknown/stale id) transparently degrades to a node-local model and
        // conversation content never leaves the machine. A blank request falls back to the node's configured default.
        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);
        var preferred = string.IsNullOrWhiteSpace(requestedModel) ? nodeSettings.DefaultModelName : requestedModel;
        var model = await _localDefaultChatModelResolver.ResolveAsync(preferred, cancellationToken);
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogInformation("Compaction skipped for conversation {ConversationId}: no installed local chat model to summarize with.", conversationId);
            return new ConversationCompactionResult { Outcome = ConversationCompactionOutcome.NoLocalModel };
        }

        // The user explicitly selected a model but it was NOT honored — it was a cloud/unknown selection, so summarization
        // ran on a node-local model instead. The UI surfaces this so the on-device downgrade is never silent.
        var usedFallbackModel = !string.IsNullOrWhiteSpace(requestedModel) && !string.Equals(model, requestedModel, StringComparison.OrdinalIgnoreCase);

        // The SAME provider-routed capability resolution the chat turn uses, on the model the fold will actually run on.
        // A miss resolves NOT-capable, which sends no thinking fields — the safe direction. Resolved here rather than
        // inside the summarizer because IModelCapabilityResolver is scoped and the summarizer is a singleton, so
        // injecting it there would capture a scoped dependency.
        var capabilities = await _modelCapabilityResolver.ResolveAsync(model, cancellationToken);

        var summary = await _summarizer
                            .SummarizeAsync(new ConversationSummarizerInput { PriorSummary = conversation.CompactionSummary, Messages = toFold, ModelName = model, SupportsThinking = capabilities.SupportsThinking },
                                cancellationToken);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return new ConversationCompactionResult { Outcome = ConversationCompactionOutcome.SummarizerReturnedNothing };
        }

        // Guard against a runaway synopsis larger than the span it replaces. Shares the summarizer's rune-safe cut so this
        // second clamp can never split a surrogate pair, whatever IConversationSummarizer implementation produced the text.
        summary = ConversationSummarizer.TruncateAtRuneBoundary(summary, Math.Max(1, _options.MaxSummaryChars));

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await _persistence
              .SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest { ConversationId = conversationId, Summary = summary, CoversToSequence = cutoffSequence, UpdatedAtUtc = now }, cancellationToken);

        _logger.LogInformation("Compacted conversation {ConversationId}: folded {Folded} message(s) up to sequence {Cutoff} into the synopsis.",
            conversationId,
            toFold.Count,
            cutoffSequence);

        return new ConversationCompactionResult { Outcome = ConversationCompactionOutcome.Compacted, Summary = summary, CoversToSequence = cutoffSequence, MessagesFolded = toFold.Count, UpdatedAtUtc = now, ModelUsed = model, UsedFallbackModel = usedFallbackModel };
    }
}
