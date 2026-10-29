namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class GraphWorkflowStore
{
    public async Task<GraphWorkflowNodeRunSnapshot?> FindConversationDecisionAsync(Guid conversationId, Guid operationId, CancellationToken cancellationToken = default)
    {
        var runs = _dbContext.GraphWorkflowRuns.AsNoTracking().Where(entity => entity.ConversationId == conversationId).Select(static entity => entity.Id);
        var nodeRun = await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                      .Where(entity => entity.DecisionOperationId == operationId && runs.Contains(entity.RunId))
                                      .OrderBy(entity => entity.RunId)
                                      .FirstOrDefaultAsync(cancellationToken);
        return nodeRun is null ? null : NodeRunSnapshot(nodeRun);
    }

    public async Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListUnpublishedNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var nodeRuns = await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                       .Where(entity => entity.RunId == runId
                                                        && entity.Status == GraphWorkflowNodeRunStatus.Succeeded
                                                        && entity.PublishedMessageId == null)
                                       .OrderBy(entity => entity.CompletedAtUtc)
                                       .ThenBy(entity => entity.NodeKey)
                                       .ToListAsync(cancellationToken);
        return [.. nodeRuns.Select(NodeRunSnapshot)];
    }

    public Task<GraphWorkflowConditionalWriteResult> MarkNodeRunPublishedAsync(Guid runId, Guid nodeRunId, Guid messageId, CancellationToken cancellationToken = default) =>
        ExecuteConditionalMutationAsync(runId,
            GraphWorkflowVersions.Any,
            async run =>
            {
                // The compare-and-set: a row somebody already stamped is simply not found, and the mutation writes nothing.
                var nodeRun = await _dbContext.GraphWorkflowNodeRuns
                                              .SingleOrDefaultAsync(entity => entity.Id == nodeRunId && entity.RunId == run.Id && entity.PublishedMessageId == null,
                                                  cancellationToken);
                if (nodeRun is null)
                {
                    return MutationOutcome.Decline(GraphWorkflowWriteDecline.NodeRunNotEligible);
                }

                nodeRun.PublishedMessageId = messageId;
                return new MutationOutcome
                {
                    EventType = GraphWorkflowEventTypes.NodePublished,
                    NodeKey = nodeRun.NodeKey,
                    DetailJson = Utf8(JsonSerializer.Serialize(new PublishedDetailPayload
                    {
                        MessageId = messageId
                    }, JsonOptions))
                };
            },
            cancellationToken,
            // The stamp moves no run state, so no run-level write needs to lose a race to it: it takes a watermark, not a version.
            bumpVersion: false);

    public Task<GraphWorkflowConditionalWriteResult> AppendNodeRunSteeringAsync(AppendGraphWorkflowSteeringCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Message, nameof(command.Message));

        return ExecuteConditionalMutationAsync(command.RunId,
            GraphWorkflowVersions.Any,
            async run =>
            {
                // Every precondition re-checked against the rows this transaction holds, so a steer racing a cancel, a settle or a second steer declines.
                if (run.Status is not (GraphWorkflowRunStatus.Running or GraphWorkflowRunStatus.WaitingForApproval))
                {
                    return MutationOutcome.Decline(GraphWorkflowWriteDecline.RunNotActive);
                }

                var nodeRun = await _dbContext.GraphWorkflowNodeRuns
                                              .SingleOrDefaultAsync(entity => entity.Id == command.NodeRunId
                                                                              && entity.RunId == run.Id
                                                                              && (entity.Status == GraphWorkflowNodeRunStatus.Queued
                                                                                  || entity.Status == GraphWorkflowNodeRunStatus.Running),
                                                  cancellationToken);
                if (nodeRun is null)
                {
                    return MutationOutcome.Decline(GraphWorkflowWriteDecline.NodeRunNotEligible);
                }

                var entries = SteeringOf(nodeRun.SteeringJson);
                if (entries.Any(entry => entry.OperationId == command.OperationId))
                {
                    return MutationOutcome.Decline(GraphWorkflowWriteDecline.OperationAlreadyRecorded);
                }

                if (entries.Count >= command.MaxEntries)
                {
                    return MutationOutcome.Decline(GraphWorkflowWriteDecline.SteeringCapReached);
                }

                nodeRun.SteeringJson = SteeringBytes([
                    .. entries, new GraphWorkflowSteeringEntry
                    {
                        OperationId = command.OperationId,
                        Message = command.Message,
                        AtUtc = Now(),
                        Attempt = nodeRun.Attempt,
                        InvocationId = nodeRun.InvocationId,
                        SteeredBySubject = command.SteeredBySubject
                    }
                ]);
                nodeRun.UpdatedAtUtc = Now();

                // Intent only: the tick that applies it writes node.steered, so the log records what happened rather than what was asked.
                return new MutationOutcome
                {
                    EventType = null,
                    NodeKey = nodeRun.NodeKey,
                    DetailJson = null
                };
            },
            cancellationToken,
            bumpVersion: false);
    }

    public Task<GraphWorkflowConditionalWriteResult> IgnoreNodeRunSteeringAsync(Guid runId, Guid nodeRunId, Guid operationId, CancellationToken cancellationToken = default) =>
        ExecuteConditionalMutationAsync(runId,
            GraphWorkflowVersions.Any,
            async run =>
            {
                var nodeRun = await _dbContext.GraphWorkflowNodeRuns.SingleOrDefaultAsync(entity => entity.Id == nodeRunId && entity.RunId == run.Id, cancellationToken);
                if (nodeRun is null || StampSteering(nodeRun, [operationId], applied: false) is not [var entry])
                {
                    return MutationOutcome.Decline(GraphWorkflowWriteDecline.NodeRunNotEligible);
                }

                nodeRun.UpdatedAtUtc = Now();
                return new MutationOutcome
                {
                    EventType = GraphWorkflowEventTypes.NodeSteerIgnored,
                    NodeKey = nodeRun.NodeKey,
                    DetailJson = Utf8(JsonSerializer.Serialize(new SteerDetailPayload
                    {
                        OperationId = entry.OperationId,
                        Message = entry.Message,
                        Attempt = entry.Attempt
                    }, JsonOptions))
                };
            },
            cancellationToken,
            bumpVersion: false);

    public async Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var nodeRuns = await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                       .Where(entity => entity.RunId == runId)
                                       .OrderBy(entity => entity.NodeKey)
                                       .ToListAsync(cancellationToken);
        return [.. nodeRuns.Select(NodeRunSnapshot)];
    }

    public async Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default)
    {
        EnsureNotBlank(nodeKey, nameof(nodeKey));
        return NodeRunSnapshot(await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                               .SingleOrDefaultAsync(entity => entity.RunId == runId && entity.NodeKey == nodeKey, cancellationToken)
                               ?? throw new GraphWorkflowNotFoundException($"Node run '{nodeKey}' was not found on graph workflow run '{runId}'."));
    }

    public Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            async run => await ApplyNodeRunTransitionAsync(run, command, cancellationToken),
            cancellationToken);
    }

    public async Task<GraphWorkflowConditionalWriteResult> DecideNodeRunAsync(DecideGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return await ExecuteConditionalMutationAsync(command.RunId,
                command.ExpectedVersion,
                async run =>
                {
                    // Re-checked INSIDE the transaction, against the run row this mutation has already taken: a cancel committing between the caller's read and
                    // this write would land a decision on a run with no tick left to route it. Declined like a lost compare-and-set — same instruction: re-read.
                    if (run.Status is not (GraphWorkflowRunStatus.Running or GraphWorkflowRunStatus.WaitingForApproval))
                    {
                        return MutationOutcome.Decline(GraphWorkflowWriteDecline.RunNotActive);
                    }

                    // The compare-and-set, expressed as the predicate the row is LOADED by: a row that is no longer
                    // waiting, or already carries a decision, simply is not found, and the mutation writes nothing.
                    var nodeRun = await _dbContext.GraphWorkflowNodeRuns
                                                  .SingleOrDefaultAsync(entity => entity.Id == command.NodeRunId
                                                                                  && entity.RunId == run.Id
                                                                                  && entity.Status == GraphWorkflowNodeRunStatus.WaitingForApproval
                                                                                  && entity.DecisionOperationId == null,
                                                      cancellationToken);
                    if (nodeRun is null)
                    {
                        return MutationOutcome.Decline(GraphWorkflowWriteDecline.NodeRunNotEligible);
                    }

                    var now = Now();

                    // BOTH answers succeed the pause. Which way the run then goes is the edges' business, read off
                    // the decision inside the output document this write stores.
                    nodeRun.Status = GraphWorkflowNodeRunStatus.Succeeded;
                    nodeRun.PendingDecisionKind = null;
                    nodeRun.DecisionOperationId = command.OperationId;
                    nodeRun.DecidedBySubject = Utf8OrNull(command.DecidedBySubject);
                    nodeRun.OutputJson = Utf8(command.OutputJson);
                    nodeRun.CompletedAtUtc = now;
                    nodeRun.UpdatedAtUtc = now;

                    // gate.decided rather than the node.completed the status alone would derive: an answered pause
                    // is a human act, and a reader following the log has to be able to find it as one.
                    return new MutationOutcome
                    {
                        EventType = GraphWorkflowEventTypes.GateDecided,
                        NodeKey = nodeRun.NodeKey,
                        DetailJson = Utf8(JsonSerializer.Serialize(new GateDecidedDetailPayload(nodeRun.NodeKey, command.Decision.ToString()), JsonOptions))
                    };
                },
                cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // The run-wide operation lookup is what normally catches a reused id; this is the same id arriving on two
            // pauses of one run CONCURRENTLY, where both passed that lookup. It is the same story to the caller.
            throw new GraphWorkflowInvalidTransitionException($"Operation '{command.OperationId}' has already decided another node run of graph workflow run "
                                                              + $"'{command.RunId}'.",
                exception);
        }
    }

    public async Task<GraphWorkflowNodeRunSnapshot?> FindNodeRunByDecisionOperationAsync(Guid runId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var nodeRun = await _dbContext.GraphWorkflowNodeRuns.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.RunId == runId && entity.DecisionOperationId == operationId, cancellationToken);
        return nodeRun is null ? null : NodeRunSnapshot(nodeRun);
    }

    /// <summary>
    ///     One node-run status move against an already-loaded run row, without the transaction and event append.
    /// </summary>
    /// <remarks>
    ///     Factored out because restart recovery applies a batch of these inside ITS transaction, and two copies of
    ///     what a transition writes would drift.
    /// </remarks>
    private async Task<MutationOutcome> ApplyNodeRunTransitionAsync(GraphWorkflowRun run,
        TransitionGraphWorkflowNodeRunCommand command,
        CancellationToken cancellationToken)
    {
        var nodeRun = await _dbContext.GraphWorkflowNodeRuns
                                      .SingleOrDefaultAsync(entity => entity.Id == command.NodeRunId && entity.RunId == run.Id, cancellationToken)
                      ?? throw new GraphWorkflowNotFoundException($"Node run '{command.NodeRunId}' was not found on graph workflow run '{run.Id}'.");
        var now = Now();

        if (command.IncrementAttempt)
        {
            // In place: one row per node key for its whole life, with the per-attempt history in the event log.
            nodeRun.Attempt++;
        }

        nodeRun.Status = command.TargetStatus;
        nodeRun.PendingDecisionKind = command.PendingDecisionKind;

        if (command.TargetStatus == GraphWorkflowNodeRunStatus.Running)
        {
            nodeRun.StartedAtUtc = now;
        }
        else if (command.TargetStatus == GraphWorkflowNodeRunStatus.Pending)
        {
            ResetToPending(nodeRun);
        }

        if (IsTerminal(command.TargetStatus))
        {
            nodeRun.CompletedAtUtc = now;
        }

        if (command.OutputJson is { } output)
        {
            nodeRun.OutputJson = Utf8(output);
        }

        if (command.InputJson is { } input)
        {
            nodeRun.InputJson = Utf8(input);
        }

        if (command.FailureClass is { } failureClass)
        {
            nodeRun.FailureClass = failureClass;
        }

        // Why a row is queued and why it ended are the same question at different moments, and a row is only ever in
        // one of those states — so they share the row's single reason column.
        if ((command.QueueReason ?? command.TerminalReason) is { } reason)
        {
            nodeRun.Error = Utf8(reason);
        }
        else if (command.TargetStatus is GraphWorkflowNodeRunStatus.Running or GraphWorkflowNodeRunStatus.Succeeded)
        {
            // A queue reason says why a row WAITED; once it runs, and above all once it succeeded, it is stale and a reader
            // would show it beside a finished node. A failure writes its own reason, so nothing a failure says is lost here.
            nodeRun.Error = null;
        }

        if (command.InvocationId is { } invocationId)
        {
            nodeRun.InvocationId = invocationId;
        }

        if (command.AppliedSteeringOperationIds.Count > 0)
        {
            _ = StampSteering(nodeRun, command.AppliedSteeringOperationIds, applied: true);
        }

        nodeRun.UpdatedAtUtc = now;
        return new MutationOutcome
        {
            EventType = command.EventType ?? EventTypeFor(command.TargetStatus),
            NodeKey = nodeRun.NodeKey,
            // The caller's detail wins: a re-attempt has cleared the failure it is re-attempting because of, so the
            // reason alone would leave its event saying nothing about what it is re-attempting.
            DetailJson = command.DetailJson is { } detail ? Utf8(detail) : ReasonDetail(command.TerminalReason)
        };
    }

    private static List<GraphWorkflowSteeringEntry> SteeringOf(byte[]? steeringJson) =>
        steeringJson is null ? [] : JsonSerializer.Deserialize<List<GraphWorkflowSteeringEntry>>(steeringJson, JsonOptions) ?? [];

    private static byte[] SteeringBytes(IReadOnlyList<GraphWorkflowSteeringEntry> entries) =>
        JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);

    /// <summary>Judges the still-unjudged entries named, and answers the ones it stamped. An entry is judged once.</summary>
    private static List<GraphWorkflowSteeringEntry> StampSteering(GraphWorkflowNodeRun nodeRun, IReadOnlyCollection<Guid> operationIds, bool applied)
    {
        var entries = SteeringOf(nodeRun.SteeringJson);
        var stamped = new List<GraphWorkflowSteeringEntry>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Applied is null && operationIds.Contains(entries[index].OperationId))
            {
                entries[index] = entries[index] with
                {
                    Applied = applied
                };
                stamped.Add(entries[index]);
            }
        }

        if (stamped.Count > 0)
        {
            nodeRun.SteeringJson = SteeringBytes(entries);
        }

        return stamped;
    }

    /// <summary>The node.steer-ignored detail: which steer arrived too late, its text, and the attempt it was aimed at.</summary>
    private sealed class SteerDetailPayload
    {
        public required Guid OperationId { get; init; }

        public required string Message { get; init; }

        public required int Attempt { get; init; }
    }

    /// <summary>The node.published detail: which chat message the result became.</summary>
    private sealed class PublishedDetailPayload
    {
        public required Guid MessageId { get; init; }
    }
}
