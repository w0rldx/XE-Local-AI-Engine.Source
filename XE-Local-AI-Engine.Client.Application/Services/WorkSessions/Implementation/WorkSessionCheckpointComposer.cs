namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>
///     Writes one checkpoint: the structured state as ids, plus the conversation synopsis as prose.
///     <para>
///         The prose half is produced by calling the EXISTING compaction service, not by a new summarizer seam. That
///         call also folds the older turns of the owned conversation into the synopsis the send path already splices on
///         every later turn, so bounding the raw history and taking the checkpoint are the same act. The structured half
///         never needs splicing — the state block is rebuilt from the database on every step.
///     </para>
///     <para>
///         Every no-op compaction outcome is non-fatal. A node with no installed local chat model cannot summarize at
///         all, and a session must still be able to checkpoint its structured state and be resumed from it.
///     </para>
/// </summary>
internal sealed class WorkSessionCheckpointComposer(
    IAgentWorkSessionStore store,
    IConversationCompactionService compaction,
    ILogger<WorkSessionCheckpointComposer> logger)
{
    private const int MaxKeyFindings = 25;

    private readonly IConversationCompactionService _compaction = compaction ?? throw new ArgumentNullException(nameof(compaction));
    private readonly ILogger<WorkSessionCheckpointComposer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IAgentWorkSessionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<WorkSessionMutationResult> ComposeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tasks = await _store.ListTasksAsync(sessionId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        var findings = await _store.ListFindingsAsync(sessionId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        var previous = await _store.GetLatestCheckpointAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var openTasks = WorkSessionStateBlockComposer.OpenTasks(tasks);
        var currentTask = WorkSessionStateBlockComposer.ResolveCurrentTask(new WorkSessionState(session, tasks, findings, [], previous));
        var state = new WorkSessionCheckpointState(currentTask?.Id,
            [.. openTasks.Select(static task => task.Id)],
            KeyFindingIds(findings),
            currentTask?.Title,
            session.StepCount);

        var summary = await SummarizeAsync(session.ConversationId, previous?.Summary, cancellationToken).ConfigureAwait(false);

        // The operation id is the checkpoint's own id, i.e. unique per call rather than derived from the step. A step
        // takes more than one checkpoint — the park-timeout one and the pause one land at the same step count — and a
        // step-derived key would make the store's idempotency swallow the second, which is the one that records where
        // the work actually stopped.
        var checkpointId = Guid.NewGuid();
        return await _store.AppendCheckpointAsync(new AppendWorkSessionCheckpointCommand(sessionId,
                                   checkpointId,
                                   WorkSessionVersions.Any,
                                   checkpointId,
                                   session.StepCount,
                                   summary,
                                   JsonSerializer.Serialize(state)),
                               cancellationToken)
                           .ConfigureAwait(false);
    }

    /// <summary>
    ///     The findings worth carrying forward by id: the decisions and open questions first, because those are what a
    ///     resumed session must not re-litigate, then the plain findings, newest first.
    /// </summary>
    private static IReadOnlyList<Guid> KeyFindingIds(IReadOnlyList<WorkSessionFindingSnapshot> findings)
    {
        return
        [
            .. findings.Where(static finding => !finding.Superseded)
                       .OrderBy(static finding => finding.Kind is AgentWorkSessionFindingKind.Decision or AgentWorkSessionFindingKind.OpenQuestion ? 0 : 1)
                       .ThenByDescending(static finding => finding.Sequence)
                       .Take(MaxKeyFindings)
                       .Select(static finding => finding.Id)
        ];
    }

    private async Task<string?> SummarizeAsync(Guid conversationId, string? previousSummary, CancellationToken cancellationToken)
    {
        // A blank requested model means "the node default", which is right here: a work-session agent pins no model, and
        // compaction stays on-node regardless of what the session itself runs on.
        // The keep window is the session one (ConversationStepContextBound.SessionKeepVerbatim), not the configured chat
        // default of eight: at eight, a session that checkpoints before its fourth step has nothing OUTSIDE the window
        // to fold, compaction answers NothingToCompact, and the checkpoint's prose half stays null — precisely on the
        // short sessions whose checkpoint is the only record of what happened. Two is safe for the same reason it is
        // safe there: everything durable is in the state block, rebuilt from the database on every step. Deliberate
        // side effect: the fold persists the synopsis and advances the send path's compaction cover, so the step after
        // a checkpoint resumes on the synopsis plus the last exchange — one on-node summarizer call per checkpoint.
        var result = await _compaction.CompactAsync(conversationId,
                                          requestedModel: null,
                                          ConversationStepContextBound.SessionKeepVerbatim,
                                          cancellationToken)
                                      .ConfigureAwait(false);

        // Any non-blank synopsis wins, not only a freshly folded one. The step boundary
        // (ConversationStepContextBound) folds this conversation with a keep window of 2 whenever it grows past the
        // budget, so by the time a checkpoint runs there is often nothing left for the configured window to fold — and
        // the "already covered" no-op returns the SYNOPSIS THAT FOLD PRODUCED. Taking only the Compacted outcome would
        // pin the checkpoint to a stale summary, or to none at all, on exactly the sessions the bound is protecting.
        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            return result.Summary;
        }

        _logger.LogDebug("Work session checkpoint kept the previous synopsis; compaction reported {Outcome}.", result.Outcome);
        return previousSummary;
    }
}
