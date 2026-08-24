namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;

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
internal sealed class WorkSessionStepContextBound(INodeChatPersistenceService persistence,
    IConversationCompactionService compaction,
    ITokenEstimator estimator,
    ILogger<WorkSessionStepContextBound> logger)
{
    /// <summary>
    ///     What a forced session compaction keeps verbatim: the previous step's state block and its answer. Two is the
    ///     service's own floor, and one step is all the verbatim history a session needs — everything durable is in the
    ///     state block, and the folded span survives as the synopsis.
    /// </summary>
    internal const int SessionKeepVerbatim = 2;

    private readonly IConversationCompactionService _compaction = compaction ?? throw new ArgumentNullException(nameof(compaction));
    private readonly ITokenEstimator _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
    private readonly ILogger<WorkSessionStepContextBound> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

    /// <summary>
    ///     Folds the session conversation's older turns when the next step would replay more than
    ///     <paramref name="budgetTokens" /> estimated tokens. A non-positive budget disables the bound; every compaction
    ///     no-op (no local model to summarize with, nothing new to fold) is non-fatal — the step still runs, and the
    ///     provider-round budgeters remain the backstop they always were.
    /// </summary>
    public async Task ApplyAsync(Guid conversationId, int budgetTokens, CancellationToken cancellationToken = default)
    {
        if (budgetTokens <= 0)
        {
            return;
        }

        var conversation = await _persistence.GetConversationForTurnAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return;
        }

        var projected = Project(conversation, _estimator);
        if (projected <= budgetTokens)
        {
            return;
        }

        var result = await _compaction.CompactAsync(conversationId, requestedModel: null, SessionKeepVerbatim, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Work session conversation {ConversationId} projected ~{Projected} replayed token(s) against a step budget of {Budget}; forced compaction reported {Outcome} after folding {Folded} message(s).",
            conversationId,
            projected,
            budgetTokens,
            result.Outcome,
            result.MessagesFolded);
    }

    /// <summary>
    ///     Estimates the tokens the next step's request will carry for HISTORY: the synopsis plus every completed,
    ///     content-bearing message the synopsis does not already cover. Mirrors
    ///     <c>NodeChatStreamService.BuildConversationContext</c> — same selected-path collapse, same anchor space, same
    ///     completed/non-empty filter, reasoning included because the runner replays it as
    ///     <see cref="TextReasoningContent" /> — and measures with the same <see cref="ITokenEstimator" /> the context
    ///     budgeters use, so this projection and their verdicts are in one arithmetic. The state block for the coming
    ///     step is deliberately NOT counted: it is bounded by construction and is what the budget exists to protect.
    /// </summary>
    internal static int Project(NodeChatConversationDto conversation, ITokenEstimator estimator)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(estimator);

        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var selected = SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath);

        var messages = new List<ChatMessage>(selected.Count + 1);
        if (CompactionContextResolver.Resolve(conversation, sortOrder: 0) is { } compaction)
        {
            messages.Add(new ChatMessage(ChatRole.User, compaction.Summary.Content));
            selected = [.. selected.Where(message => anchorSequence(message) > compaction.CoveredSequence)];
        }

        foreach (var message in selected)
        {
            if (string.IsNullOrWhiteSpace(message.Content)
                || !string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
            {
                continue;
            }

            var contents = new List<AIContent>(capacity: 2);
            if (!string.IsNullOrEmpty(message.Reasoning))
            {
                contents.Add(new TextReasoningContent(message.Reasoning));
            }

            contents.Add(new TextContent(message.Content));
            messages.Add(new ChatMessage(string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User,
                contents));
        }

        return estimator.EstimateTokens(messages);
    }
}
