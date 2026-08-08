namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IConversationCompactionService" />. Loads the conversation, picks the older span that is not
///     already covered by the existing synopsis and sits before the recent-keep window, summarizes it on-node, and
///     persists the (extended) synopsis via <see cref="INodeChatPersistenceService.SetCompactionSummaryAsync" />. The
///     original messages are untouched.
/// </summary>
internal sealed class ConversationCompactionService(
    INodeChatPersistenceService persistence,
    IConversationSummarizer summarizer,
    ILocalDefaultChatModelResolver localDefaultChatModelResolver,
    INodeSettingsStore nodeSettingsStore,
    IOptions<ConversationCompactionOptions> options,
    TimeProvider timeProvider,
    ILogger<ConversationCompactionService> logger) : IConversationCompactionService
{
    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    private readonly IConversationSummarizer _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));

    private readonly ILocalDefaultChatModelResolver _localDefaultChatModelResolver =
        localDefaultChatModelResolver ?? throw new ArgumentNullException(nameof(localDefaultChatModelResolver));

    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
    private readonly ConversationCompactionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<ConversationCompactionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<ConversationCompactionResult> CompactAsync(Guid conversationId, string? requestedModel = null, CancellationToken cancellationToken = default)
    {
        var conversation = await _persistence.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return new ConversationCompactionResult(ConversationCompactionOutcome.ConversationNotFound);
        }

        // Collapse regenerated variant siblings to the SELECTED path FIRST — exactly what the send path does via
        // SelectedPathResolver — so compaction folds only the messages the user actually chose. Without this, rejected
        // sibling answers would be merged into the synopsis while the send path later drops the selected messages through
        // the covered sequence, feeding future turns content the user never picked.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath);

        // Only completed, content-bearing messages are sendable history — the same filter the send path applies before
        // budgeting — so they are the only messages worth folding into a synopsis.
        var completed = selected
                        .Where(static message => !string.IsNullOrWhiteSpace(message.Content)
                                                 && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
                        .OrderBy(static message => message.Sequence)
                        .ToList();

        var keep = Math.Max(2, _options.RecentMessagesToKeepVerbatim);
        if (completed.Count <= keep)
        {
            return new ConversationCompactionResult(ConversationCompactionOutcome.NothingToCompact);
        }

        // Everything before the recent-keep window is foldable; the newest kept message is the first one we DON'T fold.
        var cutoffSequence = completed[completed.Count - keep - 1].Sequence;
        var priorCover = conversation.CompactionSummaryCoversToSequence;
        var toFold = completed
                     .Where(message => message.Sequence <= cutoffSequence && (priorCover is null || message.Sequence > priorCover.Value))
                     .Select(static message => new ConversationSummarizerMessage(message.Role, message.Content))
                     .ToList();

        if (toFold.Count == 0)
        {
            // The synopsis already covers everything up to the cutoff — nothing new to add.
            return new ConversationCompactionResult(ConversationCompactionOutcome.NothingToCompact,
                conversation.CompactionSummary,
                priorCover,
                MessagesFolded: 0,
                conversation.CompactionSummaryUpdatedAtUtc);
        }

        // Summarize with the model the user is chatting with when it is an installed LOCAL chat model. The resolver
        // returns the requested model iff it is a local GGUF chat model, otherwise it falls back to an installed local
        // default — so a cloud selection (or an unknown/stale id) transparently degrades to a node-local model and
        // conversation content never leaves the machine. A blank request falls back to the node's configured default.
        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var preferred = string.IsNullOrWhiteSpace(requestedModel) ? nodeSettings.DefaultModelName : requestedModel;
        var model = await _localDefaultChatModelResolver.ResolveAsync(preferred, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogInformation("Compaction skipped for conversation {ConversationId}: no installed local chat model to summarize with.", conversationId);
            return new ConversationCompactionResult(ConversationCompactionOutcome.NoLocalModel);
        }

        // The user explicitly selected a model but it was NOT honored — it was a cloud/unknown selection, so summarization
        // ran on a node-local model instead. The UI surfaces this so the on-device downgrade is never silent.
        var usedFallbackModel = !string.IsNullOrWhiteSpace(requestedModel) && !string.Equals(model, requestedModel, StringComparison.OrdinalIgnoreCase);

        var summary = await _summarizer
                            .SummarizeAsync(new ConversationSummarizerInput(conversation.CompactionSummary, toFold, model), cancellationToken)
                            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return new ConversationCompactionResult(ConversationCompactionOutcome.SummarizerReturnedNothing);
        }

        // Guard against a runaway synopsis larger than the span it replaces.
        if (summary.Length > _options.MaxSummaryChars)
        {
            summary = summary[.._options.MaxSummaryChars];
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await _persistence
              .SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest(conversationId, summary, cutoffSequence, now), cancellationToken)
              .ConfigureAwait(false);

        _logger.LogInformation("Compacted conversation {ConversationId}: folded {Folded} message(s) up to sequence {Cutoff} into the synopsis.",
            conversationId,
            toFold.Count,
            cutoffSequence);

        return new ConversationCompactionResult(ConversationCompactionOutcome.Compacted, summary, cutoffSequence, toFold.Count, now, model, usedFallbackModel);
    }
}
