namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IConversationCompactionService" />: it loads the conversation, picks the older span the
///     existing synopsis does not cover and that sits before the recent-keep window, then summarizes it on-node.
/// </summary>
/// <remarks>
///     The extended synopsis persists through
///     <see cref="INodeChatPersistenceService.SetCompactionSummaryAsync" />, leaving the originals untouched.
/// </remarks>
internal sealed class ConversationCompactionService : IConversationCompactionService
{
    private readonly INodeChatPersistenceService _persistence;
    private readonly IConversationSummarizer _summarizer;
    private readonly ILocalDefaultChatModelResolver _localDefaultChatModelResolver;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly LocalRuntimeWarmer _localRuntimeWarmer;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly ConversationCompactionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationCompactionService> _logger;

    public ConversationCompactionService(INodeChatPersistenceService persistence,
        IConversationSummarizer summarizer,
        ILocalDefaultChatModelResolver localDefaultChatModelResolver,
        IModelCapabilityResolver modelCapabilityResolver,
        LocalRuntimeWarmer localRuntimeWarmer,
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
        ArgumentNullException.ThrowIfNull(localRuntimeWarmer);
        _localRuntimeWarmer = localRuntimeWarmer;
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
        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken);
        // One budget for resolution and ALL folds, shared by manual compaction and work-session checkpoints.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(nodeSettings.MaxMessageRequestTimeoutSeconds), _timeProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        ConversationCompactionResult result;
        try
        {
            result = await CompactCoreAsync(conversationId, requestedModel, recentMessagesToKeepVerbatim, nodeSettings, operation.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Compaction timed out for conversation {ConversationId}; the previous synopsis is retained.", conversationId);
            return new ConversationCompactionResult
            {
                Outcome = ConversationCompactionOutcome.TimedOut
            };
        }

        if (result.Outcome == ConversationCompactionOutcome.Compacted)
        {
            // Commit outside the generation deadline: expiry during the store's post-write read must not report
            // TimedOut after the summary has already changed. Caller cancellation remains the store's authority.
            await _persistence.SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest
                {
                    ConversationId = conversationId,
                    Summary = result.Summary,
                    CoversToSequence = result.CoversToSequence,
                    UpdatedAtUtc = result.UpdatedAtUtc.GetValueOrDefault()
                },
                cancellationToken);
            _logger.LogInformation("Compacted conversation {ConversationId}: folded {Folded} message(s) up to sequence {Cutoff} into the synopsis.",
                conversationId, result.MessagesFolded, result.CoversToSequence);
        }

        return result;
    }

    private async Task<ConversationCompactionResult> CompactCoreAsync(Guid conversationId,
        string? requestedModel,
        int? recentMessagesToKeepVerbatim,
        StoredNodeSettings nodeSettings,
        CancellationToken cancellationToken)
    {
        var conversation = await _persistence.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return new ConversationCompactionResult
            {
                Outcome = ConversationCompactionOutcome.ConversationNotFound
            };
        }

        // Variant siblings collapse to the SELECTED path FIRST, exactly as the send path does, so compaction folds
        // only what the user chose; otherwise a rejected answer is merged while the chosen one is dropped as covered.
        var selected = SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath);

        // Ordering and folding run in ANCHOR space, as the send path does, or a late-regenerated early answer reads as
        // the newest message. The persisted CompactionSummaryCoversToSequence is therefore an anchor too.
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
            return new ConversationCompactionResult
            {
                Outcome = ConversationCompactionOutcome.NothingToCompact
            };
        }

        // Everything before the recent-keep window is foldable; the newest kept message is the first one we DON'T fold.
        var cutoffSequence = anchorSequence(completed[completed.Count - keep - 1]);
        var priorCover = conversation.CompactionSummaryCoversToSequence;
        var toFold = completed
                     .Where(message => anchorSequence(message) <= cutoffSequence && (priorCover is null || anchorSequence(message) > priorCover.Value))
                     .Select(static message => new ConversationSummarizerMessage
                     {
                         Role = message.Role,
                         Content = message.Content
                     })
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

        // Summarize with the user's model only when it is an installed LOCAL chat model: anything else degrades to a
        // node-local default, so conversation content never leaves the machine.
        var preferred = string.IsNullOrWhiteSpace(requestedModel) ? nodeSettings.DefaultModelName : requestedModel;
        var model = await _localDefaultChatModelResolver.ResolveAsync(preferred, cancellationToken);
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogInformation("Compaction skipped for conversation {ConversationId}: no installed local chat model to summarize with.", conversationId);
            return new ConversationCompactionResult
            {
                Outcome = ConversationCompactionOutcome.NoLocalModel
            };
        }

        // The user explicitly selected a model but it was NOT honored — it was a cloud/unknown selection, so summarization
        // ran on a node-local model instead. The UI surfaces this so the on-device downgrade is never silent.
        var usedFallbackModel = !string.IsNullOrWhiteSpace(requestedModel) && !string.Equals(model, requestedModel, StringComparison.OrdinalIgnoreCase);

        // The SAME provider-routed capability resolution a chat turn uses, on the model the fold will run on, where a
        // miss safely resolves NOT-capable. It is resolved here because the scoped resolver cannot live in a singleton.
        var capabilities = await _modelCapabilityResolver.ResolveAsync(model, cancellationToken);

        // The window the fold model is running with, read back exactly as a chat turn's participant on another model
        // reads it: only from an already-resident llama.cpp server, never triggering a load. Null keeps the ceiling.
        var warmProvider = await _localRuntimeWarmer.ResolveWarmableProviderAsync(model, conversationId, cancellationToken);
        var effectiveContextTokens = warmProvider is null
            ? null
            : await _localRuntimeWarmer.ResolveEffectiveContextTokensAsync(warmProvider, model, conversationId, cancellationToken);

        var summary = await _summarizer
            .SummarizeAsync(new ConversationSummarizerInput
                {
                    PriorSummary = conversation.CompactionSummary,
                    Messages = toFold,
                    ModelName = model,
                    SupportsThinking = capabilities.SupportsThinking,
                    EffectiveContextTokens = effectiveContextTokens
                },
                cancellationToken);
        // A provider may finish concurrently with cancellation; never advance coverage after the deadline.
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(summary))
        {
            return new ConversationCompactionResult
            {
                Outcome = ConversationCompactionOutcome.SummarizerReturnedNothing
            };
        }

        // Guard against a runaway synopsis larger than the span it replaces. Shares the summarizer's rune-safe cut so this
        // second clamp can never split a surrogate pair, whatever IConversationSummarizer implementation produced the text.
        summary = ConversationSummarizer.TruncateAtRuneBoundary(summary, Math.Max(1, _options.MaxSummaryChars));

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return new ConversationCompactionResult
        {
            Outcome = ConversationCompactionOutcome.Compacted,
            Summary = summary,
            CoversToSequence = cutoffSequence,
            MessagesFolded = toFold.Count,
            UpdatedAtUtc = now,
            ModelUsed = model,
            UsedFallbackModel = usedFallbackModel
        };
    }
}
