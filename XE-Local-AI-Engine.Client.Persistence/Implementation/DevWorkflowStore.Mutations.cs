namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed partial class DevWorkflowStore
{
    public Task<DevWorkflowMutationResult> TransitionRunAsync(TransitionDevWorkflowRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            async run =>
            {
                var now = Now();

                // Captured before the stamp below overwrites it: a first start and a resume are the same status move,
                // and this is the only thing that tells them apart.
                var isFirstStart = run.StartedAtUtc is null;
                if (command.TargetStatus == DevWorkflowRunStatus.Running && isFirstStart)
                {
                    run.StartedAtUtc = now;
                }

                run.Status = command.TargetStatus;
                if (command.FailureClass is not null)
                {
                    run.FailureClass = command.FailureClass;
                }

                if (command.SanitizedReason is not null)
                {
                    run.TerminalReason = command.SanitizedReason;
                }

                if (command.TargetStatus is DevWorkflowRunStatus.Completed or DevWorkflowRunStatus.Failed or DevWorkflowRunStatus.Cancelled)
                {
                    run.EndedAtUtc = now;
                }

                await ApplyWorkItemStatusAsync(run.WorkItemId, command.WorkItemStatus, cancellationToken).ConfigureAwait(false);
                return new MutationOutcome(EventTypeFor(command.TargetStatus, isFirstStart), OutcomeFor(command.TargetStatus), ReasonDetail(command.SanitizedReason));
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> MaterializeNodeRunsAsync(MaterializeDevWorkflowNodesCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.NodeRuns.Count == 0)
        {
            throw new ArgumentException("A materialization must create at least one node run.", nameof(command));
        }

        EnsureSeedsValid(command.NodeRuns, nameof(command));

        var keys = command.NodeRuns.Select(seed => seed.NodeKey).ToList();
        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            async run =>
            {
                // Checked here rather than left to the unique index: the index would surface as a DbUpdateException the
                // caller cannot tell apart from a lost race.
                if (await _dbContext.DevWorkflowNodeRuns.AnyAsync(entity => entity.RunId == run.Id && keys.Contains(entity.NodeKey), cancellationToken).ConfigureAwait(false))
                {
                    throw new DevWorkflowInvalidTransitionException($"Run '{run.Id}' already carries a node run for one of the requested node keys.");
                }

                AddNodeRuns(run, command.NodeRuns, Now());

                // A rewritten graph is the dynamic-expansion path: the run's pinned copy is the single source of
                // routing truth, so it changes here — in the same transaction as the rows it explains — and the
                // definition row is never touched, which is what keeps re-running a definition unaffected.
                if (command.GraphJson is { } rewritten)
                {
                    EnsureNotBlank(rewritten, nameof(command.GraphJson));
                    run.GraphJson = Utf8(rewritten);
                    run.GraphRevision++;
                }

                // The producer's route, re-recorded against the graph this expansion just wrote. It belongs in THIS
                // transaction: the route and the edges it describes are one fact, and a separate write afterwards
                // could leave a run whose expansion committed and whose route still denies it.
                if (command.RouteJson is { } producerRoute && command.RouteNodeRunId is { } producerNodeRunId)
                {
                    EnsureNotBlank(producerRoute, nameof(command.RouteJson));
                    var producer = await LoadNodeRunAsync(run.Id, producerNodeRunId, cancellationToken).ConfigureAwait(false);
                    producer.RouteJson = producerRoute;
                }

                var detail = Utf8(JsonSerializer.Serialize(new MaterializationDetail(command.NodeRuns.Count, run.GraphRevision), JsonOptions));
                var eventType = command.GraphJson is null ? DevWorkflowEventTypes.NodeMaterialized : DevWorkflowEventTypes.GraphChanged;
                return new MutationOutcome(eventType, $"{command.NodeRuns.Count} node run(s)", detail);
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> TransitionNodeRunAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            run => ApplyNodeRunTransitionAsync(run, command, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    ///     One node-run status move against an already-loaded run row, without the transaction and event append around
    ///     it. Factored out because restart recovery applies a batch of these inside ITS transaction, and two copies of
    ///     what a transition writes would drift.
    /// </summary>
    private async Task<MutationOutcome> ApplyNodeRunTransitionAsync(DevWorkflowRun run,
        TransitionDevWorkflowNodeRunCommand command,
        CancellationToken cancellationToken)
    {
        var nodeRun = await LoadNodeRunAsync(run.Id, command.NodeRunId, cancellationToken).ConfigureAwait(false);
        var now = Now();

        if (command.WidenMaxAttempts)
        {
            // An operator's Retry is allowed AT the cap and buys exactly one more attempt, so the cap moves with it.
            // In place, like the attempt beside it: without this the row reads "attempt 4 of 3" — the runtime saying
            // it broke its own budget where in fact a human granted one more try — and every automatic check that
            // compares Attempt against MaxAttempts would refuse the attempt the person just paid for.
            //
            // From the ATTEMPT when that is already past the cap, which is the shape a row persisted before widening
            // shipped can be in: an old operator Retry spent an attempt without moving the cap, so 4-of-3 incremented
            // to 5-of-4 and stayed one over for ever. Catching the cap up first grants the attempt instead of chasing
            // it. Saturating, because a definition may declare int.MaxValue and wrapping to negative would refuse
            // every attempt the node has left rather than buying it one more.
            var floor = Math.Max(nodeRun.MaxAttempts, nodeRun.Attempt);
            nodeRun.MaxAttempts = floor < int.MaxValue ? floor + 1 : int.MaxValue;
        }

        if (command.IncrementAttempt)
        {
            // In place: the node-run is one row per node key for its whole life, and the per-attempt history
            // lives in the event log instead.
            nodeRun.Attempt++;
        }

        if (command.ClearWorkSession)
        {
            // The resume budget goes with it: it bounds ONE attempt's session, and carrying a spent one into a
            // fresh attempt would block the new session before it had taken a step.
            nodeRun.WorkSessionId = null;
            nodeRun.SessionResumes = 0;
        }

        nodeRun.Status = command.TargetStatus;
        nodeRun.QueueReason = command.TargetStatus == DevWorkflowNodeRunStatus.Queued ? command.QueueReason : null;
        nodeRun.PendingDecisionKind = command.PendingDecisionKind;

        if (command.TargetStatus == DevWorkflowNodeRunStatus.Queued)
        {
            nodeRun.QueuedAtUtc = now;
        }
        else if (command.TargetStatus == DevWorkflowNodeRunStatus.Running)
        {
            nodeRun.StartedAtUtc = now;
        }
        else if (command.TargetStatus == DevWorkflowNodeRunStatus.Pending)
        {
            // A re-attempt starts from a clean slate. The timestamps, or the UI shows it queued since its first
            // try; the failure fields too, or a node-run reports the previous attempt's failure while it runs
            // again. What that attempt failed with is already on its node.failed event.
            nodeRun.QueuedAtUtc = null;
            nodeRun.StartedAtUtc = null;
            nodeRun.EndedAtUtc = null;
            nodeRun.FailureClass = null;
            nodeRun.TerminalReason = null;

            // The cost columns go with them, and for the same reason: they describe the attempt that just failed, and
            // leaving them would make the next attempt report the previous one's spend. What that attempt cost is
            // captured onto its node.retry.scheduled event before this reset runs.
            ClearTelemetry(nodeRun);
        }

        if (IsTerminal(command.TargetStatus))
        {
            nodeRun.EndedAtUtc = now;
        }

        if (command.OutputJson is not null)
        {
            nodeRun.OutputJson = Utf8(command.OutputJson);
        }

        if (command.InputJson is not null)
        {
            // Only the fix loop rewrites this, and only onto a re-attempt: what the node is asked to do next has to
            // carry the failure that sent the run back to it.
            nodeRun.InputJson = Utf8(command.InputJson);
        }

        if (command.FailureClass is not null)
        {
            nodeRun.FailureClass = command.FailureClass;
        }

        if (command.TerminalReason is not null)
        {
            nodeRun.TerminalReason = command.TerminalReason;
        }

        if (command.DevelopmentTaskId is { } developmentTaskId)
        {
            nodeRun.DevelopmentTaskId = developmentTaskId;
        }

        ApplyTelemetry(nodeRun, command.Telemetry);

        await ApplyWorkItemStatusAsync(run.WorkItemId, command.WorkItemStatus, cancellationToken).ConfigureAwait(false);
        return new MutationOutcome(EventTypeFor(command.TargetStatus),
            command.Outcome ?? OutcomeFor(command.TargetStatus),

            // The caller's detail wins: a re-attempt has cleared the failure fields it is re-attempting because of, so
            // the reason alone would leave its event saying nothing about what it is re-attempting.
            command.DetailJson is not null ? Utf8(command.DetailJson) : ReasonDetail(command.TerminalReason),
            nodeRun.Id);
    }

    /// <summary>
    ///     Writes the cost columns a settle collected. Member-wise and null-skipping, so a collector that could answer
    ///     only half the question — an agent node whose envelopes are gone, a structural node that has a route and
    ///     nothing else — leaves the rest of the row alone instead of blanking it.
    /// </summary>
    private static void ApplyTelemetry(DevWorkflowNodeRun nodeRun, DevWorkflowNodeTelemetry? telemetry)
    {
        if (telemetry is null)
        {
            return;
        }

        nodeRun.InputTokens = telemetry.InputTokens ?? nodeRun.InputTokens;
        nodeRun.OutputTokens = telemetry.OutputTokens ?? nodeRun.OutputTokens;
        nodeRun.ReasoningTokens = telemetry.ReasoningTokens ?? nodeRun.ReasoningTokens;
        nodeRun.EstimatedInputTokens = telemetry.EstimatedInputTokens ?? nodeRun.EstimatedInputTokens;
        nodeRun.ProviderCalls = telemetry.ProviderCalls ?? nodeRun.ProviderCalls;
        nodeRun.ToolCalls = telemetry.ToolCalls ?? nodeRun.ToolCalls;
        nodeRun.ToolSchemaTokens = telemetry.ToolSchemaTokens ?? nodeRun.ToolSchemaTokens;
        nodeRun.ToolNamesJson = telemetry.ToolNamesJson ?? nodeRun.ToolNamesJson;
        nodeRun.AgentTurnMs = telemetry.AgentTurnMs ?? nodeRun.AgentTurnMs;
        nodeRun.ModelReadinessMs = telemetry.ModelReadinessMs ?? nodeRun.ModelReadinessMs;
        nodeRun.VramFreeAtLoadBytes = telemetry.VramFreeAtLoadBytes ?? nodeRun.VramFreeAtLoadBytes;
        nodeRun.VramAdmittedBytes = telemetry.VramAdmittedBytes ?? nodeRun.VramAdmittedBytes;
        nodeRun.ServedModelName = telemetry.ServedModelName ?? nodeRun.ServedModelName;
        nodeRun.RouteJson = telemetry.RouteJson ?? nodeRun.RouteJson;
        nodeRun.WorkSessionSteps = telemetry.WorkSessionSteps ?? nodeRun.WorkSessionSteps;
    }

    /// <summary>Empties all fifteen cost columns, which is what a re-attempt's clean slate means for them.</summary>
    private static void ClearTelemetry(DevWorkflowNodeRun nodeRun)
    {
        nodeRun.InputTokens = null;
        nodeRun.OutputTokens = null;
        nodeRun.ReasoningTokens = null;
        nodeRun.EstimatedInputTokens = null;
        nodeRun.ProviderCalls = null;
        nodeRun.ToolCalls = null;
        nodeRun.ToolSchemaTokens = null;
        nodeRun.ToolNamesJson = null;
        nodeRun.AgentTurnMs = null;
        nodeRun.ModelReadinessMs = null;
        nodeRun.VramFreeAtLoadBytes = null;
        nodeRun.VramAdmittedBytes = null;
        nodeRun.ServedModelName = null;
        nodeRun.RouteJson = null;
        nodeRun.WorkSessionSteps = null;
    }

    public Task<DevWorkflowMutationResult> RouteRetryAsync(RouteDevWorkflowRetryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Route.EventType, nameof(command));
        if (command.Resets.Any(reset => reset.RunId != command.Route.RunId))
        {
            // A reset naming another run would be written against THIS run's row and version, which is the one thing
            // this method's atomicity cannot span.
            throw new ArgumentException("Every reset in a retry route must belong to the run the route names.", nameof(command));
        }

        return ExecuteMutationAsync(command.Route.RunId,
            command.Route.ExpectedVersion,
            command.Route.OperationId,
            async run =>
            {
                var outcomes = new List<MutationOutcome>(command.Resets.Count + 1)
                {
                    // FIRST, so a reader of the log sees the decision before the rows it moved, and so the operation
                    // id that makes the whole command a replay lands on the event that records the decision.
                    new(command.Route.EventType, command.Route.Outcome, Utf8OrNull(command.Route.DetailJson), command.Route.NodeRunId)
                };

                foreach (var reset in command.Resets)
                {
                    EnsureVersion(run, reset.ExpectedVersion);
                    outcomes.Add(await ApplyNodeRunTransitionAsync(run, reset, cancellationToken).ConfigureAwait(false));
                }

                return (IReadOnlyList<MutationOutcome>)outcomes;
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> AttachWorkSessionAsync(AttachDevWorkflowWorkSessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            async run =>
            {
                var nodeRun = await LoadNodeRunAsync(run.Id, command.NodeRunId, cancellationToken).ConfigureAwait(false);

                // Checked here, not left to the filtered unique index, so a second owner reads as the transition error
                // it is rather than a lost-race exception.
                if (await _dbContext.DevWorkflowNodeRuns
                                    .AnyAsync(entity => entity.WorkSessionId == command.WorkSessionId && entity.Id != nodeRun.Id, cancellationToken)
                                    .ConfigureAwait(false))
                {
                    throw new DevWorkflowInvalidTransitionException($"Work session '{command.WorkSessionId}' is already owned by another node run.");
                }

                nodeRun.WorkSessionId = command.WorkSessionId;
                if (command.CountsAsResume)
                {
                    nodeRun.SessionResumes++;
                }

                var detail = Utf8(JsonSerializer.Serialize(new WorkSessionAttachedDetail(command.WorkSessionId, nodeRun.Attempt, nodeRun.SessionResumes), JsonOptions));
                return new MutationOutcome(DevWorkflowEventTypes.WorkSessionAttached, null, detail, nodeRun.Id);
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> AppendArtifactAsync(AppendDevWorkflowArtifactCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.Name, nameof(command.Name));
        EnsureNotBlank(command.MediaType, nameof(command.MediaType));
        EnsureNotBlank(command.ContentSha256, nameof(command.ContentSha256));
        EnsureNotBlank(command.ManagedReference, nameof(command.ManagedReference));
        if (command.SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "An artifact size cannot be negative.");
        }

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            async run =>
            {
                if (await _dbContext.DevWorkflowArtifacts.AnyAsync(entity => entity.Id == command.ArtifactId, cancellationToken).ConfigureAwait(false))
                {
                    throw new DevWorkflowConcurrencyException($"Development workflow artifact '{command.ArtifactId}' already exists.");
                }

                var nodeRun = await LoadNodeRunAsync(run.Id, command.NodeRunId, cancellationToken).ConfigureAwait(false);

                // Lineage identity is (run, producing node key, name). Keying on (run, name) alone would make
                // materialized siblings — which share a template and so a logical artifact name — version each other's
                // work and mark unrelated consumers stale.
                var previous = await _dbContext.DevWorkflowArtifacts.AsNoTracking()
                                               .Where(entity => entity.RunId == run.Id
                                                                && entity.ProducingNodeKey == nodeRun.NodeKey
                                                                && entity.Name == command.Name)
                                               .OrderByDescending(entity => entity.Version)
                                               .FirstOrDefaultAsync(cancellationToken)
                                               .ConfigureAwait(false);

                var lineageId = previous?.LineageId ?? Guid.NewGuid();
                var version = (previous?.Version ?? 0) + 1;

                _dbContext.DevWorkflowArtifacts.Add(new DevWorkflowArtifact
                {
                    Id = command.ArtifactId,
                    RunId = run.Id,
                    LineageId = lineageId,
                    ProducingNodeKey = nodeRun.NodeKey,
                    ProducedByNodeRunId = nodeRun.Id,
                    Name = command.Name,
                    Version = version,
                    Kind = command.Kind,
                    MediaType = command.MediaType,
                    ContentSha256 = command.ContentSha256,
                    SizeBytes = command.SizeBytes,
                    ManagedReference = command.ManagedReference,
                    IsValid = true,
                    IsStale = false,
                    Sequence = NextSequence(run),
                    CreatedAtUtc = Now()
                });

                if (previous is null)
                {
                    return new MutationOutcome(DevWorkflowEventTypes.ArtifactCreated, command.Kind.ToString(), DetailJson: null, nodeRun.Id);
                }

                // The superseded row and its bytes both stay: versioning is the point. The reference travels on the
                // event and the result so the caller that owns the blob store can decide what to sweep.
                var detail = Utf8(JsonSerializer.Serialize(new ArtifactSupersessionDetail(previous.Id, previous.ManagedReference, version), JsonOptions));
                return new MutationOutcome(DevWorkflowEventTypes.ArtifactSuperseded, command.Kind.ToString(), detail, nodeRun.Id, previous.Id);
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> RecordArtifactUsesAsync(RecordDevWorkflowArtifactUsesCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ArtifactIds.Count == 0)
        {
            throw new ArgumentException("An artifact-use record cannot be empty.", nameof(command));
        }

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            async run =>
            {
                var nodeRun = await LoadNodeRunAsync(run.Id, command.NodeRunId, cancellationToken).ConfigureAwait(false);
                var wanted = command.ArtifactIds.Distinct().ToList();

                var known = await _dbContext.DevWorkflowArtifacts.AsNoTracking()
                                            .Where(entity => entity.RunId == run.Id && wanted.Contains(entity.Id))
                                            .Select(entity => entity.Id)
                                            .ToListAsync(cancellationToken)
                                            .ConfigureAwait(false);
                if (known.Count != wanted.Count)
                {
                    throw new DevWorkflowNotFoundException($"One or more artifacts recorded as consumed by node run '{nodeRun.Id}' do not belong to run '{run.Id}'.");
                }

                var already = await _dbContext.DevWorkflowArtifactUses.AsNoTracking()
                                              .Where(entity => entity.NodeRunId == nodeRun.Id && wanted.Contains(entity.ArtifactId))
                                              .Select(entity => entity.ArtifactId)
                                              .ToListAsync(cancellationToken)
                                              .ConfigureAwait(false);

                var added = 0;
                foreach (var artifactId in wanted.Except(already))
                {
                    _dbContext.DevWorkflowArtifactUses.Add(new DevWorkflowArtifactUse
                    {
                        Id = Guid.NewGuid(),
                        RunId = run.Id,
                        NodeRunId = nodeRun.Id,
                        ArtifactId = artifactId,
                        RecordedSequence = NextSequence(run)
                    });
                    added++;
                }

                return new MutationOutcome(DevWorkflowEventTypes.ArtifactUsed,
                    null,
                    Utf8(JsonSerializer.Serialize(new ArtifactUseDetail(added), JsonOptions)),
                    nodeRun.Id);
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> MarkDependentsStaleAsync(MarkDevWorkflowStaleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.StaleReason, nameof(command.StaleReason));

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            async run =>
            {
                // Checked rather than left to return MarkedCount: 0, which is what a genuinely unconsumed artifact also
                // reports — a caller passing an id from the wrong run should see the mistake, not a plausible zero.
                if (!await _dbContext.DevWorkflowArtifacts
                                     .AnyAsync(entity => entity.Id == command.SupersededArtifactId && entity.RunId == run.Id, cancellationToken)
                                     .ConfigureAwait(false))
                {
                    throw new DevWorkflowNotFoundException($"Development workflow artifact '{command.SupersededArtifactId}' does not belong to run '{run.Id}'.");
                }

                // Pure DB work over the recorded uses: this marks, it never regenerates. Everything a node-run produced
                // is flagged when that node-run consumed the version a newer one just superseded.
                var consumers = await _dbContext.DevWorkflowArtifactUses.AsNoTracking()
                                                .Where(entity => entity.RunId == run.Id && entity.ArtifactId == command.SupersededArtifactId)
                                                .Select(entity => entity.NodeRunId)
                                                .ToListAsync(cancellationToken)
                                                .ConfigureAwait(false);

                // The superseding artifact is excluded explicitly: a re-attempt of a node that consumed its own prior
                // version is a consumer of the thing it just replaced, so without this the new version marks itself
                // stale the moment it lands.
                var dependents = consumers.Count == 0
                    ? []
                    : await _dbContext.DevWorkflowArtifacts
                                      .Where(entity => entity.RunId == run.Id
                                                       && consumers.Contains(entity.ProducedByNodeRunId)
                                                       && entity.Id != command.SupersedingArtifactId
                                                       && !entity.IsStale)
                                      .ToListAsync(cancellationToken)
                                      .ConfigureAwait(false);

                // The value the event about to be appended will take. Stamping the mark with it means "stale as of the
                // same watermark that records why", so a client replaying from a cursor sees both or neither.
                var sequence = run.LastSequence + 1;
                foreach (var dependent in dependents)
                {
                    dependent.IsStale = true;
                    dependent.StaleSinceSequence = sequence;
                    dependent.StaleBecauseArtifactId = command.SupersedingArtifactId;
                    dependent.StaleReason = command.StaleReason;
                }

                var detail = Utf8(JsonSerializer.Serialize(new StaleMarkDetail(command.SupersededArtifactId, command.SupersedingArtifactId, dependents.Count), JsonOptions));
                return new MutationOutcome(DevWorkflowEventTypes.ArtifactStaleMarked, command.StaleReason, detail);
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> RecordDecisionAsync(RecordDevWorkflowDecisionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            async run =>
            {
                var nodeRun = await LoadNodeRunAsync(run.Id, command.NodeRunId, cancellationToken).ConfigureAwait(false);

                // What the caller validated against, re-read under the transaction. Checked BEFORE the one-per-attempt
                // rule below, which reads the row's CURRENT attempt and so would see a moved row as simply undecided.
                if ((command.ExpectedAttempt is { } expectedAttempt && nodeRun.Attempt != expectedAttempt)
                    || (command.ExpectedStatus is { } expectedStatus && nodeRun.Status != expectedStatus))
                {
                    throw new DevWorkflowConcurrencyException($"Node run '{nodeRun.Id}' is attempt {nodeRun.Attempt} and {nodeRun.Status}, "
                                                              + $"but the {command.Decision} was taken on attempt {command.ExpectedAttempt} "
                                                              + $"and {command.ExpectedStatus}.");
                }

                // One decision per node-run ATTEMPT: a node-run legitimately accumulates several over its life, but not
                // two for the same try. The standing one is loaded rather than merely counted, because the caller that
                // gets this refusal has to be able to say WHAT was already decided.
                if (await _dbContext.DevWorkflowDecisions.AsNoTracking()
                                    .FirstOrDefaultAsync(entity => entity.NodeRunId == nodeRun.Id && entity.Attempt == nodeRun.Attempt, cancellationToken)
                                    .ConfigureAwait(false) is { } standing)
                {
                    throw new DevWorkflowGateAlreadyDecidedException($"Node run '{nodeRun.Id}' already carries a decision for attempt {nodeRun.Attempt}.",
                        standing.Decision);
                }

                if (command is { Decision: DevWorkflowDecisionKind.Retry, MaxTotalAttempts: { } budget })
                {
                    await EnsureRetryBudgetAsync(run.Id, budget, cancellationToken).ConfigureAwait(false);
                }

                _dbContext.DevWorkflowDecisions.Add(new DevWorkflowDecision
                {
                    Id = command.DecisionId,
                    RunId = run.Id,
                    NodeRunId = nodeRun.Id,
                    Attempt = nodeRun.Attempt,
                    Decision = command.Decision,
                    Comment = Utf8OrNull(command.Comment),
                    PayloadJson = Utf8OrNull(command.PayloadJson),
                    DecidedBySubject = command.DecidedBySubject,
                    OperationId = command.OperationId,
                    Sequence = NextSequence(run),
                    DecidedAtUtc = Now()
                });

                // The decision lands; moving the node-run out of its wait is the runtime's next step, so the pending
                // marker is cleared here and the status is not guessed at.
                nodeRun.PendingDecisionKind = null;
                return new MutationOutcome(DevWorkflowEventTypes.GateDecided, command.Decision.ToString(), DetailJson: null, nodeRun.Id);
            },
            cancellationToken);
    }

    public Task<DevWorkflowMutationResult> AppendEventAsync(AppendDevWorkflowEventCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBlank(command.EventType, nameof(command.EventType));

        return ExecuteMutationAsync(command.RunId,
            command.ExpectedVersion,
            command.OperationId,
            _ => Task.FromResult(new MutationOutcome(command.EventType, command.Outcome, Utf8OrNull(command.DetailJson), command.NodeRunId)),
            cancellationToken);
    }
}
