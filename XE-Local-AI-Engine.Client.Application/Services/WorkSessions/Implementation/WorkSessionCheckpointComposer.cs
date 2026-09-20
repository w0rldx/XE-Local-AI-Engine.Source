namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;

/// <summary>
///     Writes one checkpoint: the structured state as ids, plus the conversation synopsis as prose.
/// </summary>
/// <remarks>
///     The prose half comes from the EXISTING compaction service rather than a new summarizer seam, and that one call
///     also folds the owned conversation's older turns, so bounding the raw history and taking the checkpoint are the
///     same act. Every no-op compaction outcome is non-fatal: a node with no installed local chat model cannot
///     summarize at all, and a session must still checkpoint its structured state and be resumed from it.
/// </remarks>
internal sealed class WorkSessionCheckpointComposer
{
    private const int MaxKeyFindings = 25;

    private readonly IConversationCompactionService _compaction;
    private readonly ILogger<WorkSessionCheckpointComposer> _logger;
    private readonly IAgentWorkSessionStore _store;

    public WorkSessionCheckpointComposer(
        IAgentWorkSessionStore store,
        IConversationCompactionService compaction,
        ILogger<WorkSessionCheckpointComposer> logger)
    {
        ArgumentNullException.ThrowIfNull(compaction);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(store);
        _compaction = compaction;
        _logger = logger;
        _store = store;
    }

    public async Task<WorkSessionMutationResult> ComposeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _store.GetAsync(sessionId, cancellationToken);
        var tasks = await _store.ListTasksAsync(sessionId, sinceSequence: 0, cancellationToken);
        var findings = await _store.ListFindingsAsync(sessionId, sinceSequence: 0, cancellationToken);
        var previous = await _store.GetLatestCheckpointAsync(sessionId, cancellationToken);

        var openTasks = WorkSessionStateBlockComposer.OpenTasks(tasks);
        var currentTask = WorkSessionStateBlockComposer.ResolveCurrentTask(new WorkSessionState { Session = session, Tasks = tasks, Findings = findings, Artifacts = [], LastCheckpoint = previous });
        var state = new WorkSessionCheckpointState(currentTask?.Id,
            [.. openTasks.Select(static task => task.Id)],
            KeyFindingIds(findings),
            currentTask?.Title,
            session.StepCount);

        var summary = await SummarizeAsync(session.ConversationId, previous?.Summary, cancellationToken);

        // Unique per call, never derived from the step: a step takes more than one checkpoint (park-timeout, then
        // pause) and a step-derived key lets idempotency swallow the second — the one recording where work stopped.
        var checkpointId = Guid.NewGuid();
        return await _store.AppendCheckpointAsync(new AppendWorkSessionCheckpointCommand
        {
            SessionId = sessionId,
            CheckpointId = checkpointId,
            ExpectedVersion = WorkSessionVersions.Any,
            OperationId = checkpointId,
            Step = session.StepCount,
            Summary = summary,
            StateJson = JsonSerializer.Serialize(state)
        },
                               cancellationToken);
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
        // A blank requested model keeps compaction on the node default whatever the session runs on. The keep window is
        // the SESSION one, not the configured chat eight — wiki 04-agent-mode.md ("Checkpoints, and what a repoint…").
        var result = await _compaction.CompactAsync(conversationId,
                                          requestedModel: null,
                                          ConversationStepContextBound.SessionKeepVerbatim,
                                          cancellationToken);

        // Any non-blank synopsis wins, not only a freshly folded one: the step boundary often leaves nothing to fold,
        // and its "already covered" no-op returns the synopsis THAT fold produced. Compacted-only would pin a stale one.
        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            return result.Summary;
        }

        _logger.LogDebug("Work session checkpoint kept the previous synopsis; compaction reported {Outcome}.", result.Outcome);
        return previousSummary;
    }
}
