namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>Bounds the transcript one work-session step replays.</summary>
/// <remarks>
///     Before each send it projects what the next step will replay and, over budget, folds the older turns into the
///     synopsis the send path already splices (<see cref="CompactionContextResolver" />). Nothing durable is lost: the
///     state block is rebuilt from the database every step (<see cref="WorkSessionStateBlockComposer" />), which is why
///     the raw transcript is the expendable half. Why the checkpoint's own compaction is not this bound, and the other
///     two bounds beside it: docs/wiki/04-agent-mode.md ("The transcript bound at the step boundary").
/// </remarks>
internal sealed class ConversationStepContextBound
{
    /// <summary>What a forced session compaction keeps verbatim: the previous step's state block and its answer.</summary>
    /// <remarks>
    ///     Two is the compaction service's own floor, and one step is all the verbatim history a session needs —
    ///     everything durable is in the state block, and the folded span survives as the synopsis.
    /// </remarks>
    internal const int SessionKeepVerbatim = 2;

    private readonly IConversationCompactionService _compaction;
    private readonly ITokenEstimator _estimator;
    private readonly ILogger<ConversationStepContextBound> _logger;
    private readonly INodeChatPersistenceService _persistence;

    public ConversationStepContextBound(INodeChatPersistenceService persistence,
        IConversationCompactionService compaction,
        ITokenEstimator estimator,
        ILogger<ConversationStepContextBound> logger)
    {
        ArgumentNullException.ThrowIfNull(compaction);
        ArgumentNullException.ThrowIfNull(estimator);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(persistence);
        _compaction = compaction;
        _estimator = estimator;
        _logger = logger;
        _persistence = persistence;
    }

    /// <summary>
    ///     Folds the session conversation's older turns when the next step would replay more than
    ///     <paramref name="budgetTokens" /> estimated tokens; a non-positive budget disables the bound.
    /// </summary>
    /// <remarks>
    ///     A compaction no-op (no local model to summarize with, nothing new to fold) is non-fatal: the step still runs
    ///     and the provider-round budgeters stay the backstop. The keep window is two for a work session, whose state
    ///     block is rebuilt every step, and the chat window for an integration session, whose transcript IS its state;
    ///     it sits after the token because an <c>int</c> before it rebinds the supervisor's fourth positional argument.
    /// </remarks>
    /// <param name="effectiveModel">From <c>WorkSessionToolGate.InspectAllowListAsync</c>; null falls back to the transcript's.</param>
    /// <param name="cancellationToken">Cancels the reads; the compaction itself is the caller's own token.</param>
    /// <param name="keepVerbatimExchanges">How many of the newest messages a forced fold keeps verbatim.</param>
    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
        Justification =
            "The keep window has to sit AFTER the token: the supervisor's call site passes four positional arguments whose fourth is CancellationToken.None, "
            + "so an int inserted before the token silently rebinds it and does not compile. The rule's own exemption for trailing optional parameters does not "
            + "apply here only because effectiveModel is optional too, which makes the token not the last REQUIRED parameter.")]
    public async Task ApplyAsync(Guid conversationId,
        int budgetTokens,
        string? effectiveModel = null,
        CancellationToken cancellationToken = default,
        int keepVerbatimExchanges = SessionKeepVerbatim,
        bool includeToolHistory = false,
        int toolResultExcerptChars = ConversationContextBudgetOptions.DefaultHistoricalToolResultExcerptChars)
    {
        if (budgetTokens <= 0)
        {
            return;
        }

        // With tool history on this takes the FULL read: the capped turn read omits the metadata_json the projection
        // must count, so it would measure a transcript smaller than the one the turn sends.
        var conversation = includeToolHistory
            ? await _persistence.GetConversationAsync(conversationId, cancellationToken)
            : await _persistence.GetConversationForTurnAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        // ONE arithmetic: the same resolved model gives the estimate its divisor and the budget its observed correction
        // (tighten-only, that ALONE — TokenEstimatorCalibrationStore.EstimateSafetyFactor is a context-window reserve).
        var modelName = effectiveModel ?? ResolveTranscriptModel(conversation);
        var projected = Project(conversation, _estimator, modelName, includeToolHistory, toolResultExcerptChars);
        var effectiveBudget = TokenEstimatorCalibrationStore.ApplyObservedCorrection(budgetTokens, _estimator.ResolveObservedCorrection(modelName));
        if (projected <= effectiveBudget)
        {
            return;
        }

        // The FOLD runs on the node's default chat model, not the step's, whether the pin is the caller's or the bound
        // agent's: summarizing is not the session's work, and a pinned model would contend for its own load slot.
        var result = await _compaction.CompactAsync(conversationId, requestedModel: null, keepVerbatimExchanges, cancellationToken);
        _logger.LogInformation(
            "Work session conversation {ConversationId} projected ~{Projected} replayed token(s) against a step budget of {Budget} (effective {EffectiveBudget} after this model's observed correction); forced compaction reported {Outcome} after folding {Folded} message(s).",
            conversationId,
            projected,
            budgetTokens,
            effectiveBudget,
            result.Outcome,
            result.MessagesFolded);
    }

    /// <summary>
    ///     Estimates the tokens the next step's request will carry for HISTORY: the synopsis plus every completed,
    ///     content-bearing message the synopsis does not already cover.
    /// </summary>
    /// <remarks>
    ///     Mirrors <c>ConversationContextBuilder.Build</c> — same selected-path collapse, anchor space,
    ///     completed/non-empty filter, unanswered notice and blanked failed turn — and measures with the same <see cref="ITokenEstimator" /> and calibration the
    ///     context budgeters use, so projection and verdict are one arithmetic. The coming step's state block is not
    ///     counted: it is bounded by construction and is what the budget protects. Reasoning always is, even where the
    ///     provider drops it — docs/wiki/04-agent-mode.md ("The transcript bound at the step boundary").
    /// </remarks>
    /// <param name="conversation">The session's owned conversation, as the send path will read it.</param>
    /// <param name="estimator">The same estimator the context budgeters measure with.</param>
    /// <param name="modelName">The coming step's model, for its calibrated divisor; null falls back to the transcript's.</param>
    internal static int Project(NodeChatConversationDto conversation,
        ITokenEstimator estimator,
        string? modelName = null,
        bool includeToolHistory = false,
        int toolResultExcerptChars = ConversationContextBudgetOptions.DefaultHistoricalToolResultExcerptChars)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(estimator);

        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var selected = SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath);
        // The send path marks a request whose answer failed, read before compaction drops anything, so count it too.
        var unanswered = ConversationContextBuilder.FindUnansweredUserTurns(selected, anchorSequence);

        var messages = new List<ChatMessage>(selected.Count + 1);

        // The turns the send path keeps below the cutoff ONLY for their exchanges: it blanks their text and reasoning,
        // so counting either here would measure a request the turn will not send.
        HashSet<Guid>? exchangeOnlySurvivors = null;
        if (CompactionContextResolver.Resolve(conversation, sortOrder: 0) is { } compaction)
        {
            messages.Add(new ChatMessage(ChatRole.User, compaction.Summary.Content));

            // The SAME cutoff exemption the send path applies: a turn that completed a tool call survives the fold for
            // its exchanges, so the estimate counts what the turn will actually carry.
            var kept = new List<NodeChatPersistedMessageDto>(selected.Count);
            foreach (var message in selected)
            {
                if (anchorSequence(message) > compaction.CoveredSequence)
                {
                    kept.Add(message);
                }
                else if (ConversationContextBuilder.SurvivesCompactionForToolHistory(message, includeToolHistory))
                {
                    kept.Add(message);
                    _ = (exchangeOnlySurvivors ??= []).Add(message.MessageId);
                }
            }

            selected = kept;
        }

        foreach (var message in selected)
        {
            // The SAME projection the send path applies, so the estimate counts what the turn carries: with tool
            // history on, completed exchanges replay as real function contents, and a turn kept only for them stays.
            var exchanges = includeToolHistory
                ? ConversationContextBuilder.ProjectSendableToolExchanges(message, toolResultExcerptChars)
                : null;

            // The send path also blanks a failed turn it keeps for its exchanges: its partial text is never sent.
            var exchangeOnly = exchangeOnlySurvivors?.Contains(message.MessageId) == true
                               || (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) && ConversationContextBuilder.IsFailedAnswer(message));
            var sendable = !string.IsNullOrWhiteSpace(message.Content)
                           && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal);
            if (!sendable && exchanges is null)
            {
                continue;
            }

            if (exchanges is { Count: > 0 })
            {
                ConversationToolExchangeMessages.Append(messages, exchanges);
            }

            var contents = new List<AIContent>(capacity: 2);
            if (!exchangeOnly && !string.IsNullOrEmpty(message.Reasoning))
            {
                contents.Add(new TextReasoningContent(message.Reasoning));
            }

            if (!exchangeOnly && !string.IsNullOrEmpty(message.Content))
            {
                contents.Add(new TextContent(ConversationContextBuilder.WithUnansweredNotice(message, unanswered)));
            }

            if (contents.Count == 0)
            {
                continue;
            }

            messages.Add(new ChatMessage(string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User,
                contents));
        }

        return estimator.EstimateTokens(messages, modelName ?? ResolveTranscriptModel(conversation));
    }

    /// <summary>
    ///     FALLBACK only: the model the most recent completed assistant message ran on, or null before the session has
    ///     answered at all.
    /// </summary>
    /// <remarks>
    ///     Null on the first step, where the transcript is too short for the divisor to matter. Read from the whole
    ///     message list rather than the selected path — an unchosen variant still ran on the same model. It describes
    ///     the LAST turn's model, which is not necessarily the next one's: see <see cref="ApplyAsync" />.
    /// </remarks>
    private static string? ResolveTranscriptModel(NodeChatConversationDto conversation)
    {
        string? model = null;
        var latest = long.MinValue;
        foreach (var message in conversation.Messages)
        {
            if (string.IsNullOrWhiteSpace(message.Model)
                || !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal)
                || message.Sequence <= latest)
            {
                continue;
            }

            latest = message.Sequence;
            model = message.Model;
        }

        return model;
    }
}
