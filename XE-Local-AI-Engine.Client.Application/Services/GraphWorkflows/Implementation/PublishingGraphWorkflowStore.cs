namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Announces every committed graph-workflow mutation, and forwards everything else untouched.</summary>
/// <remarks>
///     The publish sits HERE rather than at each call site: a missed one is a pane that silently stops updating and no
///     test would notice, and every mutation returns the watermark its commit allocated, so wrapping the one interface
///     they all go through makes it impossible to forget, later code included. The kind comes from the COMMAND, not
///     the event row — a caller parking a node run on a human is asking for a person, the one push with a consequence
///     beyond a re-render. simplified: one ping per mutation, no coalescing; a per-run debounce goes here if it measures.
/// </remarks>
internal sealed class PublishingGraphWorkflowStore : IGraphWorkflowStore
{
    private readonly IGraphWorkflowStore _inner;
    private readonly ILogger<PublishingGraphWorkflowStore> _logger;
    private readonly IGraphWorkflowEventPublisher _publisher;

    public PublishingGraphWorkflowStore(
        IGraphWorkflowStore inner,
        IGraphWorkflowEventPublisher publisher,
        ILogger<PublishingGraphWorkflowStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(publisher);
        _inner = inner;
        _logger = logger;
        _publisher = publisher;
    }

    public Task<GraphWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateDefinitionAsync(command, cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateDefinitionAsync(command, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListDefinitionsAsync(cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.GetDefinitionAsync(definitionId, cancellationToken);

    /// <summary>A definition edit is not a run change: nobody subscribes to a definition, so it announces nothing.</summary>
    public Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.DeleteDefinitionAsync(definitionId, cancellationToken);

    /// <summary>Nothing is subscribed to a run that does not exist yet, so a start publishes nothing.</summary>
    public Task<GraphWorkflowRunSnapshot> StartRunAsync(StartGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        _inner.StartRunAsync(command, cancellationToken);

    public Task<GraphWorkflowRunSnapshot?> FindRunByRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        _inner.FindRunByRequestAsync(requestId, cancellationToken);

    public Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.GetRunAsync(runId, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        _inner.ListRunsAsync(status, limit, cancellationToken);

    public Task<int> CountActiveRunsAsync(int probeLimit, CancellationToken cancellationToken = default) =>
        _inner.CountActiveRunsAsync(probeLimit, cancellationToken);

    public Task<GraphWorkflowMutationResult> TransitionRunAsync(TransitionGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.TransitionRunAsync(command, cancellationToken), GraphWorkflowChangeKind.Run, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.ListNodeRunsAsync(runId, cancellationToken);

    public Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default) =>
        _inner.GetNodeRunAsync(runId, nodeKey, cancellationToken);

    public Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A node run entering a human wait is the one status move a client does more than repaint for.
        var kind = command.TargetStatus is GraphWorkflowNodeRunStatus.WaitingForApproval
            ? GraphWorkflowChangeKind.Gate
            : GraphWorkflowChangeKind.Node;
        return PublishAsync(_inner.TransitionNodeRunAsync(command, cancellationToken), kind, cancellationToken);
    }

    /// <summary>
    ///     An answered pause is the change a watching client most needs told: it is what removes the badge asking for a
    ///     person. A write that matched no row announces nothing, because nothing committed.
    /// </summary>
    public async Task<GraphWorkflowMutationResult?> DecideNodeRunAsync(DecideGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        if (await _inner.DecideNodeRunAsync(command, cancellationToken) is not { } result)
        {
            return null;
        }

        return await PublishAsync(Task.FromResult(result), GraphWorkflowChangeKind.Gate, cancellationToken);
    }

    public Task<GraphWorkflowNodeRunSnapshot?> FindNodeRunByDecisionOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        _inner.FindNodeRunByDecisionOperationAsync(runId, operationId, cancellationToken);

    public Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.AppendEventAsync(command, cancellationToken), GraphWorkflowChangeKind.Run, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long afterSeq = 0,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        _inner.ListEventsAsync(runId, afterSeq, limit, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListInterruptedNodeRunsAsync(cancellationToken);

    /// <summary>
    ///     Startup recovery, before any client can be watching: the rows it touches are re-read whole by whoever
    ///     subscribes afterwards.
    /// </summary>
    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default) =>
        _inner.ReconcileNonTerminalNodeRunsAsync(sanitizedReason, verdicts, unjudged, cancellationToken);

    /// <summary>Awaits the mutation, then announces the watermark that commit allocated.</summary>
    /// <remarks>
    ///     A failed announcement is logged and swallowed: the write is already committed, and failing the caller over
    ///     a notification would turn a late repaint into a lost transition. The subscriber re-reads the run from its
    ///     own watermark, so the missed ping costs a refresh and nothing else.
    /// </remarks>
    private async Task<GraphWorkflowMutationResult> PublishAsync(Task<GraphWorkflowMutationResult> mutation,
        GraphWorkflowChangeKind kind,
        CancellationToken cancellationToken)
    {
        var result = await mutation;
        try
        {
            await _publisher.PublishAsync(result.RunId, result.Sequence, kind, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Graph workflow run {RunId} committed a {Kind} change at sequence {Sequence} that could not be announced; subscribers re-read on their next ping.",
                result.RunId,
                kind,
                result.Sequence);
        }

        return result;
    }
}
