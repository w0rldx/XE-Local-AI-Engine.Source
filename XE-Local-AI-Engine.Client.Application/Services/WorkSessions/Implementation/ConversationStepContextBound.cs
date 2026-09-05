namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Bounds the transcript one work-session step replays.
///     <para>
///         A step is an ordinary chat turn, so the send path re-sends every earlier step's user state block, assistant
///         text and assistant reasoning verbatim. Nothing folds them: compaction has no trigger of its own, and the only
///         session caller is the checkpoint, which lands every <c>CheckpointEveryNSteps</c> steps and keeps the
///         configured eight messages — four whole steps — verbatim. The transcript therefore grows monotonically and
///         eats the headroom the step's OWN tool loop needs; a research step reading one knowledge-base document spends
///         some 16k tokens on that result alone. On 2026-08-24 a 27B model at a 65,536-token window went over at step 5.
///     </para>
///     <para>
///         So: before each send, project what the next step will replay and, over budget, fold it into the synopsis the
///         send path already splices (<see cref="CompactionContextResolver" />). Nothing is lost — the state block is
///         rebuilt from the database every step (<see cref="WorkSessionStateBlockComposer" />), which is precisely why
///         the raw transcript is the expendable half.
///     </para>
/// </summary>
internal sealed class ConversationStepContextBound(
    INodeChatPersistenceService persistence,
    IConversationCompactionService compaction,
    ITokenEstimator estimator,
    ILogger<ConversationStepContextBound> logger)
{
    /// <summary>
    ///     What a forced session compaction keeps verbatim: the previous step's state block and its answer. Two is the
    ///     service's own floor, and one step is all the verbatim history a session needs — everything durable is in the
    ///     state block, and the folded span survives as the synopsis.
    /// </summary>
    internal const int SessionKeepVerbatim = 2;

    private readonly IConversationCompactionService _compaction = compaction ?? throw new ArgumentNullException(nameof(compaction));
    private readonly ITokenEstimator _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
    private readonly ILogger<ConversationStepContextBound> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

    /// <summary>
    ///     Folds the session conversation's older turns when the next step would replay more than
    ///     <paramref name="budgetTokens" /> estimated tokens. A non-positive budget disables the bound; every compaction
    ///     no-op (no local model to summarize with, nothing new to fold) is non-fatal — the step still runs, and the
    ///     provider-round budgeters remain the backstop they always were.
    ///     <para>
    ///         The projection and the budget are held in ONE arithmetic: the model is resolved once and used both for
    ///         the estimate's divisor and for the observed correction the budget is divided by. Correcting only the
    ///         estimate would compare calibrated tokens against an uncalibrated number and fold late on exactly the
    ///         models calibration exists to protect. Tighten-only and neutral until a round has been recorded, so an
    ///         uncalibrated session compares against the configured budget unchanged. Note this uses the correction
    ///         ALONE — <see cref="TokenEstimatorCalibrationStore.EstimateSafetyFactor" /> is a context-window reserve
    ///         and has no business retuning a flat policy budget.
    ///     </para>
    /// </summary>
    /// <param name="conversationId">The session's owned conversation.</param>
    /// <param name="budgetTokens">The configured step budget; non-positive disables the bound.</param>
    /// <param name="effectiveModel">
    ///     The model the UPCOMING turn will run on, as the supervisor already resolved it for this step
    ///     (<c>WorkSessionToolGate.InspectAllowListAsync</c>'s verdict). It, not the transcript, is what the estimate
    ///     and the budget must be calibrated under: a paused session repointed to another agent — or an unpinned agent
    ///     whose node default model changed — runs the next step on a DIFFERENT model, and the previous model's divisor
    ///     and correction would fold late on exactly the case the bound exists to prevent. Null (the agent was deleted,
    ///     or the gate could not be read) falls back to the transcript's model.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads; the compaction itself is the caller's own token.</param>
    /// <param name="keepVerbatimExchanges">
    ///     How many of the newest messages a forced fold keeps verbatim. The default is the work-session floor of two,
    ///     so the supervisor's call site is byte-identical, and it exists because a work-session step rebuilds its state
    ///     block from the database every step — the transcript beyond the previous step carries nothing the model still
    ///     needs.
    ///     <para>
    ///         An integration session has no state block: its transcript IS the session state. Folded to two, the first
    ///         turn over budget would collapse a caller-managed session to a synopsis plus the last exchange and delete
    ///         the continuation the feature exists to deliver — so the integration coordinator passes the CHAT window
    ///         instead. It sits LAST, after the token, deliberately: an <c>int</c> inserted before it rebinds the
    ///         supervisor's fourth positional argument (<c>CancellationToken.None</c>) and does not compile.
    ///     </para>
    /// </param>
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

        // With tool history on this takes the FULL read: the parts the projection has to count live in the same
        // metadata_json blob the capped turn read omits for every non-user row the synopsis covers, so the capped read
        // would measure a transcript smaller than the one the turn sends — the exact failure this bound prevents.
        var conversation = includeToolHistory
            ? await _persistence.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false)
            : await _persistence.GetConversationForTurnAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return;
        }

        var modelName = effectiveModel ?? ResolveTranscriptModel(conversation);
        var projected = Project(conversation, _estimator, modelName, includeToolHistory, toolResultExcerptChars);
        var effectiveBudget = TokenEstimatorCalibrationStore.ApplyObservedCorrection(budgetTokens, _estimator.ResolveObservedCorrection(modelName));
        if (projected <= effectiveBudget)
        {
            return;
        }

        // The FOLD runs on the node's default chat model, not on the one the step itself uses — true for a session's
        // caller pin exactly as it already is for a bound agent's own. Summarizing is not the session's work, and
        // routing it to a pinned model would make a fold contend for that model's load slot mid-session.
        var result = await _compaction.CompactAsync(conversationId, requestedModel: null, keepVerbatimExchanges, cancellationToken).ConfigureAwait(false);
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
    ///     content-bearing message the synopsis does not already cover. Mirrors
    ///     <c>ConversationContextBuilder.Build</c> — same selected-path collapse, same anchor space, same
    ///     completed/non-empty filter — and measures with the same <see cref="ITokenEstimator" /> the context budgeters
    ///     use, under the same per-model calibration, so this projection and their verdicts are in one arithmetic. The
    ///     state block for the coming step is deliberately NOT counted: it is bounded by construction and is what the
    ///     budget exists to protect.
    ///     <para>
    ///         Reasoning is counted, and on a llama.cpp session it is counted for a request that will NOT carry it.
    ///         That is deliberate and it is the conservative direction. Verified against
    ///         Microsoft.Extensions.AI.OpenAI 10.9.0: the Chat Completions client's content-part conversion handles
    ///         text, URI, data and hosted-file content only, so a historical <see cref="TextReasoningContent" /> is
    ///         dropped on the floor rather than sent. Only the Responses API client (Codex) replays it — and MUST, so
    ///         no suppression seam belongs here. Over-counting a Chat-Completions provider by the reasoning it will
    ///         discard makes this bound fire slightly early; under-counting a Responses-API one would make it fire too
    ///         late, which is the failure it exists to prevent.
    ///     </para>
    /// </summary>
    /// <param name="conversation">The session's owned conversation, as the send path will read it.</param>
    /// <param name="estimator">The same estimator the context budgeters measure with.</param>
    /// <param name="modelName">
    ///     The model the coming step will run on, so the estimate uses that model's calibrated divisor rather than the
    ///     uncalibrated chars/4 default. The supervisor supplies it from the tool gate's verdict for THIS step. Null
    ///     falls back to the transcript — the model the LAST completed assistant message ran on — which is right only
    ///     while nothing has repointed the session or moved the node default since, and is therefore a fallback for
    ///     when no upcoming model is resolvable rather than the primary source.
    /// </param>
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
            // The SAME projection the send path applies, so the estimate counts what the turn will actually carry: with
            // tool history on, a turn's completed exchanges are replayed as real function contents (both estimators
            // count those natively), and a turn kept only for them is kept here too.
            var exchanges = includeToolHistory
                ? ConversationContextBuilder.ProjectSendableToolExchanges(message, toolResultExcerptChars)
                : null;

            var exchangeOnly = exchangeOnlySurvivors?.Contains(message.MessageId) == true;
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
                contents.Add(new TextContent(message.Content));
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
    ///     FALLBACK only: the model the most recent completed assistant message ran on, or null when the session has
    ///     not answered yet (the first step, where the transcript is short enough that the divisor cannot matter). Read
    ///     from the whole message list rather than the selected path: a variant that was not chosen still ran on the
    ///     same model. It describes the model the LAST turn used, which is not necessarily the next one's — see the
    ///     <c>effectiveModel</c> parameter on <see cref="ApplyAsync" />.
    /// </summary>
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
