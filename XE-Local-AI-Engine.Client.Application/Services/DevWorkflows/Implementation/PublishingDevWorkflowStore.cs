namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Announces every committed workflow mutation, and forwards everything else untouched.
///     <para>
///         The publish sits HERE rather than at each of the fifteen call sites in the runtime for one reason: a missed
///         call site is a pane that silently stops updating, and there is no test that would notice. Every mutation
///         returns the watermark its commit allocated, so wrapping the one interface they all go through makes the
///         notification impossible to forget — including from code written later.
///     </para>
///     <para>
///         The change kind comes from the COMMAND, not from the event row: a caller that transitions a node run into a
///         human wait is asking for a person, and that is the one push with a consequence beyond re-rendering.
///     </para>
/// </summary>
/// <remarks>
///     ponytail: one ping per committed mutation, with no coalescing window. Slice A's graphs are linear, so a tick
///     writes one or two; a parallel stage would want a debounce here, keyed by run id, before the client turns each
///     ping into a refetch.
/// </remarks>
internal sealed class PublishingDevWorkflowStore(IDevWorkflowStore inner, IDevWorkflowEventPublisher publisher) : IDevWorkflowStore
{
    private readonly IDevWorkflowStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IDevWorkflowEventPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

    public Task<DevWorkflowWorkItemSnapshot> CreateWorkItemAsync(CreateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateWorkItemAsync(command, cancellationToken);

    public Task<DevWorkflowWorkItemSnapshot> UpdateWorkItemAsync(UpdateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateWorkItemAsync(command, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowWorkItemSnapshot>> ListWorkItemsAsync(DevWorkflowWorkItemStatus? status = null,
        CancellationToken cancellationToken = default) =>
        _inner.ListWorkItemsAsync(status, cancellationToken);

    public Task<DevWorkflowWorkItemSnapshot> GetWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
        _inner.GetWorkItemAsync(workItemId, cancellationToken);

    public Task<DevWorkflowWorkItemDeletion> DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default) =>
        _inner.DeleteWorkItemAsync(workItemId, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateDefinitionAsync(command, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateDefinitionAsync(command, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowDefinitionSummary>> ListDefinitionsAsync(bool includeArchived = false, CancellationToken cancellationToken = default) =>
        _inner.ListDefinitionsAsync(includeArchived, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.GetDefinitionAsync(definitionId, cancellationToken);

    public Task<DevWorkflowDefinitionSnapshot> ArchiveDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _inner.ArchiveDefinitionAsync(definitionId, cancellationToken);

    // Rule sets are forwarded unwrapped, like the definitions above them: no run's pane renders one, and the runtime
    // consumes a rule set only at the NEXT materialization — whose own node-run write publishes a Run-kind ping.
    public Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(CreateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateRuleSetAsync(command, cancellationToken);

    public Task<DevWorkflowRuleSetSnapshot> UpdateRuleSetAsync(UpdateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default) =>
        _inner.UpdateRuleSetAsync(command, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRuleSetSummary>> ListRuleSetsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListRuleSetsAsync(cancellationToken);

    public Task<DevWorkflowRuleSetSnapshot> GetRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default) =>
        _inner.GetRuleSetAsync(ruleSetId, cancellationToken);

    public Task DeleteRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default) =>
        _inner.DeleteRuleSetAsync(ruleSetId, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRuleSetSnapshot>> ListEnabledRuleSetsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListEnabledRuleSetsAsync(cancellationToken);

    /// <summary>Nothing is subscribed to a run that does not exist yet, so a start publishes nothing.</summary>
    public Task<DevWorkflowRunSnapshot> StartRunAsync(StartDevWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        _inner.StartRunAsync(command, cancellationToken);

    public Task<DevWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.GetRunAsync(runId, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRunSnapshot>> ListRunsAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        _inner.ListRunsAsync(workItemId, status, limit, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        _inner.ListRunSummariesAsync(workItemId, status, limit, cancellationToken);

    public Task<DevWorkflowMutationResult> TransitionRunAsync(TransitionDevWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.TransitionRunAsync(command, cancellationToken), DevWorkflowChangeKind.Run, cancellationToken);

    /// <summary>
    ///     Startup recovery, before any client can be watching: the run rows it touches are re-read whole by whoever
    ///     subscribes afterwards.
    /// </summary>
    public Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<DevWorkflowNodeRunVerdict> verdicts,
        DevWorkflowUnjudgedNodeRunBlock? unjudged = null,
        CancellationToken cancellationToken = default) =>
        _inner.ReconcileNonTerminalNodeRunsAsync(sanitizedReason, verdicts, unjudged, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListInterruptedNodeRunsAsync(cancellationToken);

    public Task<DevWorkflowMutationResult> MaterializeNodeRunsAsync(MaterializeDevWorkflowNodesCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.MaterializeNodeRunsAsync(command, cancellationToken), DevWorkflowChangeKind.Node, cancellationToken);

    public Task<DevWorkflowMutationResult> TransitionNodeRunAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A node run entering a human wait is the one status move a client does more than repaint for.
        var kind = command.TargetStatus is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked
            ? DevWorkflowChangeKind.Gate
            : DevWorkflowChangeKind.Node;
        return PublishAsync(_inner.TransitionNodeRunAsync(command, cancellationToken), kind, cancellationToken);
    }

    /// <summary>
    ///     ONE announcement for the whole route, because it is one commit. Its watermark names the routing event, and
    ///     every reset the same transaction wrote sits after it, so a subscriber replaying from there still sees them.
    /// </summary>
    public Task<DevWorkflowMutationResult> RouteRetryAsync(RouteDevWorkflowRetryCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.RouteRetryAsync(command, cancellationToken), DevWorkflowChangeKind.Node, cancellationToken);

    public Task<DevWorkflowMutationResult> AttachWorkSessionAsync(AttachDevWorkflowWorkSessionCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.AttachWorkSessionAsync(command, cancellationToken), DevWorkflowChangeKind.Node, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.ListNodeRunsAsync(runId, cancellationToken);

    public Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default) =>
        _inner.GetNodeRunAsync(nodeRunId, cancellationToken);

    public Task<Guid?> FindRunIdForDevelopmentTaskAsync(Guid developmentTaskId, CancellationToken cancellationToken = default) =>
        _inner.FindRunIdForDevelopmentTaskAsync(developmentTaskId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, Guid>> FindRunIdsForDevelopmentTasksAsync(IReadOnlyList<Guid> developmentTaskIds,
        CancellationToken cancellationToken = default) =>
        _inner.FindRunIdsForDevelopmentTasksAsync(developmentTaskIds, cancellationToken);

    public Task<DevWorkflowMutationResult> AppendArtifactAsync(AppendDevWorkflowArtifactCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.AppendArtifactAsync(command, cancellationToken), DevWorkflowChangeKind.Artifact, cancellationToken);

    public Task<DevWorkflowMutationResult> RecordArtifactUsesAsync(RecordDevWorkflowArtifactUsesCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.RecordArtifactUsesAsync(command, cancellationToken), DevWorkflowChangeKind.Artifact, cancellationToken);

    public Task<DevWorkflowMutationResult> MarkDependentsStaleAsync(MarkDevWorkflowStaleCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.MarkDependentsStaleAsync(command, cancellationToken), DevWorkflowChangeKind.Artifact, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default) =>
        _inner.ListArtifactsAsync(runId, sinceSequence, cancellationToken);

    public Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default) =>
        _inner.GetArtifactAsync(artifactId, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListConsumedArtifactIdsAsync(Guid nodeRunId, CancellationToken cancellationToken = default) =>
        _inner.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken);

    public Task<DevWorkflowMutationResult> RecordDecisionAsync(RecordDevWorkflowDecisionCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.RecordDecisionAsync(command, cancellationToken), DevWorkflowChangeKind.Gate, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowDecisionSnapshot>> ListDecisionsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _inner.ListDecisionsAsync(runId, cancellationToken);

    public Task<DevWorkflowDecisionSnapshot?> FindDecisionByOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        _inner.FindDecisionByOperationAsync(runId, operationId, cancellationToken);

    /// <summary>A read: nothing committed, so there is nothing to announce.</summary>
    public Task<string?> FindOperationEventTypeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        _inner.FindOperationEventTypeAsync(runId, operationId, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListOwnedWorkSessionIdsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListOwnedWorkSessionIdsAsync(cancellationToken);

    public Task<DevWorkflowMutationResult> AppendEventAsync(AppendDevWorkflowEventCommand command, CancellationToken cancellationToken = default) =>
        PublishAsync(_inner.AppendEventAsync(command, cancellationToken), DevWorkflowChangeKind.Run, cancellationToken);

    public Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        _inner.ListEventsAsync(runId, sinceSequence, limit, cancellationToken);

    private async Task<DevWorkflowMutationResult> PublishAsync(Task<DevWorkflowMutationResult> mutation,
        DevWorkflowChangeKind kind,
        CancellationToken cancellationToken)
    {
        var result = await mutation.ConfigureAwait(false);
        await _publisher.PublishAsync(result.RunId, result.Sequence, kind, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
