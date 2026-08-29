namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The agent lane: one work session per agent node-run attempt, driven by the run that owns it.
///     <para>
///         The runtime is a CLIENT of the work-session machinery, not a fork of it — the stepwise executor, the
///         checkpoints, the transcript and the pause/restart/resume are all one level down and already proven. What this
///         adds is the four things a graph needs from them: an objective composed from the node's inputs, an admission
///         that queues honestly when the node's one invocation slot is taken, a poll that turns a session status into a
///         node-run status, and the promotion of what the session produced into the run's own audit.
///     </para>
///     <para>
///         It writes node-run transitions itself, from inside the dispatcher's serialized tick — which is what keeps the
///         "every node-run status write happens inside <c>AdvanceOnceAsync</c>" invariant true. It never starts a task
///         of its own: the work session is already detached, so there is nothing here to detach.
///     </para>
/// </summary>
internal sealed class DevWorkflowAgentExecutor
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentDefinitionStore _agents;
    private readonly ILogger<DevWorkflowAgentExecutor> _logger;
    private readonly DevWorkflowOptions _options;
    private readonly DevWorkflowArtifactPromotion _promotion;
    private readonly IAgentWorkSessionStore _sessionStore;
    private readonly IWorkflowOwnedWorkSessionLifecycle _sessions;

    public DevWorkflowAgentExecutor(IWorkflowOwnedWorkSessionLifecycle sessions,
        IAgentWorkSessionStore sessionStore,
        IAgentDefinitionStore agents,
        DevWorkflowArtifactPromotion promotion,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowAgentExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    /// <summary>
    ///     Admits an eligible agent node run, and answers how many transitions it wrote.
    ///     <para>
    ///         The row goes to <c>Queued</c> first, always, even when a slot is free a line later. It costs one event and
    ///         it is what makes the queue honest: three parallel agent nodes on a one-slot node are
    ///         <c>Running, Queued, Queued</c>, and a reader has to be able to see that rather than infer it.
    ///     </para>
    /// </summary>
    public async Task<int> DispatchAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (await TryReadAttachedAsync(nodeRun, cancellationToken).ConfigureAwait(false) is
            { Status: AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Failed or AgentWorkSessionStatus.Cancelled })
        {
            // The session landed and the host died before the poll wrote what it said. Nothing needs re-running — the
            // row is settled off the session's own answer, which is exactly what that tick would have written. A retry
            // does not come through here: it releases its session first, precisely so it cannot.
            DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                nodeRun.Id,
                                DevWorkflowVersions.Any,
                                DevWorkflowNodeRunStatus.Running),
                            cancellationToken)
                        .ConfigureAwait(false);
            return 1 + await PollAsync(store, run, nodeRun with { Status = DevWorkflowNodeRunStatus.Running }, nodeRuns, cancellationToken).ConfigureAwait(false);
        }

        var written = 0;
        if (nodeRun.Status == DevWorkflowNodeRunStatus.Pending)
        {
            DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Queued, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                nodeRun.Id,
                                DevWorkflowVersions.Any,
                                DevWorkflowNodeRunStatus.Queued,
                                QueueReason: DevWorkflowQueueReasons.AwaitingAgentSlot),
                            cancellationToken)
                        .ConfigureAwait(false);
            written++;
        }

        if (!_sessions.HasCapacity)
        {
            // Queueing, not failure: nothing is wrong, the node's one slot is simply held. No event, no failure class —
            // the row's reason says what it is waiting for and the next tick asks again.
            return written;
        }

        WorkSessionDetail session;
        try
        {
            session = await ResolveSessionAsync(store, graph, run, node, nodeRun, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkSessionValidationException exception)
        {
            // A missing agent, a model that cannot call tools, a node with work sessions switched off. A retry produces
            // the same answer, so it goes straight to a human with the message verbatim — it is already sanitized and
            // it already names the fix.
            return written + await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DevWorkflowValidationException exception)
        {
            return written + await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!await TryDriveAsync(session, cancellationToken).ConfigureAwait(false))
        {
            // Lost the admission race between the capacity read and the start. The row stays Queued with its reason and
            // keeps the session it already owns, so the next tick starts that one rather than creating a second.
            return written;
        }

        DevWorkflowStateMachine.EnsureLegal(DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowNodeRunStatus.Running),
                        cancellationToken)
                    .ConfigureAwait(false);
        return written + 1;
    }

    /// <summary>
    ///     Reads what the node run's session is doing and settles the row when it has landed, answering how many
    ///     transitions it wrote.
    ///     <para>
    ///         The session status is the only authority: this never remembers what it dispatched, which is exactly why a
    ///         restart costs nothing but the poll that follows it.
    ///     </para>
    /// </summary>
    public async Task<int> PollAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (nodeRun.WorkSessionId is not { } sessionId)
        {
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    DevWorkflowFailureClasses.Internal,
                    "This node run is running without a work session, so nothing can report what it is doing.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var session = await TryReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    DevWorkflowFailureClasses.Configuration,
                    "The work session this node run was driving no longer exists.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        switch (session.Status)
        {
            case AgentWorkSessionStatus.Completed:
                return await SucceedAsync(store, run, nodeRun, nodeRuns, session, cancellationToken).ConfigureAwait(false);

            case AgentWorkSessionStatus.Failed:
                return await SettleAsync(store,
                        run,
                        nodeRun,
                        nodeRuns,
                        DevWorkflowNodeRunStatus.Failed,
                        DevWorkflowFailureClasses.ProviderError,
                        "The agent's work session failed.",
                        FailureOutput(nodeRun, session, DevWorkflowFailureClasses.ProviderError),
                        cancellationToken)
                    .ConfigureAwait(false);

            case AgentWorkSessionStatus.Cancelled:
                return await SettleAsync(store,
                        run,
                        nodeRun,
                        nodeRuns,
                        DevWorkflowNodeRunStatus.Cancelled,
                        DevWorkflowFailureClasses.Cancelled,
                        "The agent's work session was cancelled.",
                        FailureOutput(nodeRun, session, DevWorkflowFailureClasses.Cancelled),
                        cancellationToken)
                    .ConfigureAwait(false);

            case AgentWorkSessionStatus.Draft or AgentWorkSessionStatus.Paused or AgentWorkSessionStatus.Interrupted:

                // Never while the run is draining: under a pause the session was paused ON PURPOSE a moment ago, and
                // resuming it here would undo the operator's command with the run still reading Pausing.
                return run.Status is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling
                    ? 0
                    : await ResumeAsync(store, run, nodeRun, session, cancellationToken).ConfigureAwait(false);

            default:
                // Running, or parked on a question it asked. Still working; nothing to write.
                return 0;
        }
    }

    /// <summary>
    ///     Asks the session to stop, for whichever drain the run is in. The row is deliberately NOT settled here: only
    ///     the session knows where it lands, and the next tick's poll writes that rather than this guessing it.
    /// </summary>
    public async Task StopAsync(Guid sessionId, bool cancel, CancellationToken cancellationToken)
    {
        try
        {
            _ = cancel
                ? await _sessions.CancelAsync(sessionId, cancellationToken).ConfigureAwait(false)
                : await _sessions.PauseAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WorkSessionInvalidTransitionException or WorkSessionNotFoundException)
        {
            // Already settled, or already gone. Either way there is nothing left to stop and the poll reads the truth.
            _logger.LogDebug(exception, "Work session {SessionId} could not be stopped by its owning run; it has already settled.", sessionId);
        }
    }

    /// <summary>
    ///     The session this attempt owns: the one already attached if it can still be driven, otherwise a fresh one.
    ///     <para>
    ///         A retry always gets a NEW session — resuming the one that just failed resumes its poisoned context — but a
    ///         session that is merely <c>Draft</c>, <c>Paused</c> or <c>Interrupted</c> is the crash window between the
    ///         attach and the start, or a pause, and reusing it is what keeps a restart from stranding a conversation
    ///         nobody will ever drive.
    ///     </para>
    /// </summary>
    private async Task<WorkSessionDetail> ResolveSessionAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        if (await TryReadAttachedAsync(nodeRun, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var agentDefinitionId = await ResolveAgentAsync(node, cancellationToken).ConfigureAwait(false);
        var objective = await ComposeObjectiveAsync(store, graph, run, node, nodeRun, cancellationToken).ConfigureAwait(false);
        var created = await _sessions.CreateAsync(node.Label, objective, agentDefinitionId, cancellationToken).ConfigureAwait(false);

        try
        {
            _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(run.Id,
                                nodeRun.Id,
                                DevWorkflowVersions.Any,
                                created.Id,
                                DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "attach")),
                            cancellationToken)
                        .ConfigureAwait(false);
        }
        catch
        {
            // Until the attach commits, NOTHING points at this session: the next tick creates another, a work-item
            // delete cannot find it, and the external lifecycle refuses a workflow-kind session to every other caller.
            // So the create is undone here rather than left for the startup sweep, and the original failure is what
            // propagates — the compensation is not the story.
            await ReleaseUnattachedAsync(store, created.Id).ConfigureAwait(false);
            throw;
        }

        return created;
    }

    /// <summary>
    ///     Deletes a session no node run owns, and leaves an owned one alone.
    ///     <para>
    ///         Ownership is re-read across ALL node runs rather than assumed from the failure, and both directions
    ///         matter: an attach can commit and still throw on the way back — a cancellation between the commit and
    ///         the return — and an attach can fail precisely BECAUSE another node run already owns that session.
    ///         Deleting in either case would take a transcript out from under a row that points at it.
    ///     </para>
    ///     <para>
    ///         Runs without a cancellation token, because this is the cleanup for a call that may itself have been
    ///         cancelled.
    ///     </para>
    /// </summary>
    private async Task ReleaseUnattachedAsync(IDevWorkflowStore store, Guid sessionId)
    {
        try
        {
            if ((await store.ListOwnedWorkSessionIdsAsync(CancellationToken.None).ConfigureAwait(false)).Contains(sessionId))
            {
                return;
            }

            await _sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Reported, never rethrown: the caller's failure is the one worth surfacing, and a session left here is
            // exactly what the startup sweep is for.
            _logger.LogWarning(exception, "Work session {SessionId} could not be released after its attach failed.", sessionId);
        }
    }

    /// <summary>
    ///     Starts or resumes the session, whichever its status calls for. Answers <see langword="false" /> when the node
    ///     refused the admission — which is a queue, not a failure.
    /// </summary>
    private async Task<bool> TryDriveAsync(WorkSessionDetail session, CancellationToken cancellationToken)
    {
        try
        {
            _ = session.Status switch
            {
                AgentWorkSessionStatus.Draft => await _sessions.StartAsync(session.Id, cancellationToken).ConfigureAwait(false),
                AgentWorkSessionStatus.Paused or AgentWorkSessionStatus.Interrupted => await _sessions.ResumeAsync(session.Id, cancellationToken).ConfigureAwait(false),

                // Already being driven — the crash window between the start and the node run's own Running write.
                _ => session
            };
            return true;
        }
        catch (WorkSessionInvalidTransitionException exception)
        {
            _logger.LogDebug(exception, "Work session {SessionId} was not admitted; its node run stays queued.", session.Id);
            return false;
        }
    }

    private async Task<Guid> ResolveAgentAsync(DevWorkflowGraphNode node, CancellationToken cancellationToken)
    {
        if (node.AgentDefinitionId is { } bound)
        {
            return bound;
        }

        if (node.AgentSeedSlug is not { } slug)
        {
            throw new DevWorkflowValidationException($"Agent node '{node.NodeKey}' binds no agent definition, so nothing can run it.");
        }

        var seeded = await _agents.GetBySeedSlugAsync(slug, cancellationToken).ConfigureAwait(false);
        return seeded?.Id ?? throw new DevWorkflowValidationException($"Agent node '{node.NodeKey}' binds the seeded agent '{slug}', which this node does not have.");
    }

    /// <summary>
    ///     What the agent is asked to do: the node's own instructions, the operator's request, and the artifacts the
    ///     nodes before it produced — recorded as consumed in the same breath, so the audit says what this attempt was
    ///     given rather than what a later read can guess.
    /// </summary>
    private static async Task<string> ComposeObjectiveAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        var objective = new StringBuilder();
        _ = objective.AppendLine(node.Instructions is { Length: > 0 } instructions ? instructions : $"Carry out the '{node.Label}' step of this development workflow.");

        if (ReadInput(nodeRun.InputJson) is { Count: > 0 } input)
        {
            _ = objective.AppendLine().AppendLine("## What was asked");
            foreach (var (name, value) in input)
            {
                _ = objective.AppendLine(CultureInfo.InvariantCulture, $"- {name}: {value}");
            }
        }

        var upstream = await DevWorkflowUpstreamArtifacts.RecordAsync(store, graph, run, nodeRun, cancellationToken).ConfigureAwait(false);
        if (upstream.Count > 0)
        {
            _ = objective.AppendLine().AppendLine("## What the steps before you produced");
            foreach (var artifact in upstream)
            {
                _ = objective.AppendLine(CultureInfo.InvariantCulture, $"- {artifact.Kind} '{artifact.Name}' (version {artifact.Version}, id {artifact.Id})");
            }
        }

        return objective.ToString().TrimEnd();
    }

    /// <summary>
    ///     The node run's input document as flat name/value lines. Nested and array values are rendered as their raw
    ///     JSON: a caller-supplied <c>inputsJson</c> is arbitrary, and reformatting it would be inventing structure it
    ///     does not have.
    /// </summary>
    private static IReadOnlyList<(string Name, string Value)> ReadInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return
            [
                .. document.RootElement.EnumerateObject()
                           .Where(static property => property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                           .Select(static property => (property.Name,
                               property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText()))
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<int> SucceedAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        WorkSessionDetail session,
        CancellationToken cancellationToken)
    {
        // Evidence first, status last — the same order the work-session loop uses one level down. A crash in that
        // window re-derives the same answer, because the promotion is keyed and the poll runs again.
        var promoted = await _promotion.PromoteAsync(run, nodeRun, session.Id, cancellationToken).ConfigureAwait(false);
        var findings = await _sessionStore.ListFindingsAsync(session.Id, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        var output = JsonSerializer.Serialize(new AgentOutput(DevWorkflowNodeOutputStatuses.Succeeded,
                nodeRun.Attempt,
                FailureClass: null,
                JsonNamingPolicy.CamelCase.ConvertName(session.Status.ToString()),
                nodeRun.SessionResumes,
                promoted,
                findings.Where(static finding => !finding.Superseded)
                        .GroupBy(static finding => finding.Kind)
                        .ToDictionary(static group => JsonNamingPolicy.CamelCase.ConvertName(group.Key.ToString()), static group => group.Count(), StringComparer.Ordinal)),
            JsonOptions);

        return await SettleAsync(store, run, nodeRun, nodeRuns, DevWorkflowNodeRunStatus.Succeeded, failureClass: null, terminalReason: null, output, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Resumes a session that parked, until the node run's resume budget is spent.
    ///     <para>
    ///         Parking is routine rather than a fault: a work session pauses on its own step budget, and a workflow node
    ///         routinely needs more steps than one run allows. Exhausting the budget therefore asks a human rather than
    ///         failing the node — the work so far is on the session, and a person decides whether it needs more.
    ///     </para>
    /// </summary>
    private async Task<int> ResumeAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        WorkSessionDetail session,
        CancellationToken cancellationToken)
    {
        if (nodeRun.SessionResumes >= _options.MaxSessionResumesPerNodeRun)
        {
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    DevWorkflowFailureClasses.BudgetExhausted,
                    $"This node run resumed its work session {nodeRun.SessionResumes} times without finishing, which is as many as this node allows.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_sessions.HasCapacity || !await TryDriveAsync(session, cancellationToken).ConfigureAwait(false))
        {
            // The slot is held by another session. The row stays Running — it has not stopped working, it is waiting for
            // its own continuation — and the next tick asks again.
            return 0;
        }

        // Recorded AFTER the resume landed, and keyed by the resume index so a replayed tick cannot spend the budget
        // twice. The attach event is also the per-attempt history the single-row node-run schema does not keep.
        _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            session.Id,
                            DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, $"resume-{nodeRun.SessionResumes}"),
                            CountsAsResume: true),
                        cancellationToken)
                    .ConfigureAwait(false);
        return 1;
    }

    /// <summary>The session the row is still carrying, if it has one and it still exists.</summary>
    private async Task<WorkSessionDetail?> TryReadAttachedAsync(DevWorkflowNodeRunSnapshot nodeRun, CancellationToken cancellationToken) =>
        nodeRun.WorkSessionId is { } attached ? await TryReadAsync(attached, cancellationToken).ConfigureAwait(false) : null;

    private async Task<WorkSessionDetail?> TryReadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            return await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkSessionNotFoundException)
        {
            return null;
        }
    }

    private static string FailureOutput(DevWorkflowNodeRunSnapshot nodeRun, WorkSessionDetail session, string failureClass) =>
        JsonSerializer.Serialize(new AgentOutput(DevWorkflowNodeOutputStatuses.Failed,
                nodeRun.Attempt,
                failureClass,
                JsonNamingPolicy.CamelCase.ConvertName(session.Status.ToString()),
                nodeRun.SessionResumes,
                ArtifactCount: 0,
                new Dictionary<string, int>(StringComparer.Ordinal)),
            JsonOptions);

    private static async Task<int> SettleAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowNodeRunStatus target,
        string? failureClass,
        string? terminalReason,
        string outputJson,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, target, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            target,
                            OutputJson: outputJson,
                            FailureClass: failureClass,
                            TerminalReason: terminalReason,
                            WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, nodeRuns, nodeRun.Id, target)),
                        cancellationToken)
                    .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     Stands the node run down for a human. The work item is blocked unconditionally rather than recomputed: ANY
    ///     blocked node run blocks its item, whatever the rest of the graph is doing.
    /// </summary>
    private static async Task<int> BlockAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string failureClass,
        string sanitizedReason,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Blocked, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowNodeRunStatus.Blocked,
                            PendingDecisionKind: DevWorkflowDecisionKind.Abandon,
                            FailureClass: failureClass,
                            TerminalReason: sanitizedReason,
                            WorkItemStatus: DevWorkflowWorkItemStatus.Blocked),
                        cancellationToken)
                    .ConfigureAwait(false);
        return 1;
    }

    /// <summary>The agent node's slice of the output document every executor writes (§5.5 of the runtime plan).</summary>
    private sealed record AgentOutput(
        string Status,
        int Attempt,
        string? FailureClass,
        string SessionStatus,
        int SessionResumes,
        int ArtifactCount,
        IReadOnlyDictionary<string, int> Findings);
}
