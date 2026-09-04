namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

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

    /// <summary>
    ///     The ceiling this composition holds itself to, in characters.
    ///     <para>
    ///         <b>Coupled to <c>WorkSessionService.MaxObjectiveLength</c> (8000), which is private to that class.</b> The
    ///         work-session layer REFUSES an over-long objective rather than trimming it, and that refusal reaches here
    ///         as a validation failure that blocks the node run for a human — so an objective this lane composes must
    ///         never approach it. The margin absorbs a modest reduction there without this noticing;
    ///         <c>TheObjectiveLimit_IsTheOneTheWorkSessionLayerActuallyEnforces</c> fails if the two ever cross.
    ///     </para>
    ///     <para>
    ///         Only the ARTIFACT phase is bounded by it. Instructions and inputs are appended first and uncapped: a node
    ///         whose own instructions exceed the work-session limit is the pre-existing refusal, and silently trimming
    ///         what an author wrote would be a worse answer than the block.
    ///     </para>
    /// </summary>
    internal const int MaxObjectiveCharacters = 7000;

    /// <summary>
    ///     The largest artifact whose bytes are worth reading to fill an objective, in bytes.
    ///     <para>
    ///         Two orders of magnitude above anything <see cref="MaxObjectiveCharacters" /> could use, and two below the
    ///         64 MiB an artifact is allowed to be: the point is not to pick the smallest workable number but to keep a
    ///         node with several large upstream artifacts from reading — and decoding — all of them to keep a few
    ///         thousand characters of the first.
    ///     </para>
    /// </summary>
    private const int MaxInjectableArtifactBytes = 256 * 1024;

    private readonly IAgentDefinitionStore _agents;
    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly ILogger<DevWorkflowAgentExecutor> _logger;
    private readonly DevWorkflowOptions _options;
    private readonly DevWorkflowArtifactPromotion _promotion;
    private readonly DevWorkflowRetryPolicy _retries;
    private readonly IAgentWorkSessionStore _sessionStore;
    private readonly IWorkflowOwnedWorkSessionLifecycle _sessions;
    private readonly WorkSessionWriteDeclarationGuard _writes;

    public DevWorkflowAgentExecutor(IWorkflowOwnedWorkSessionLifecycle sessions,
        IAgentWorkSessionStore sessionStore,
        IAgentDefinitionStore agents,
        WorkSessionWriteDeclarationGuard writes,
        DevWorkflowArtifactPromotion promotion,
        IDevWorkflowArtifactBlobStore blobs,
        DevWorkflowRetryPolicy retries,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowAgentExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _retries = retries ?? throw new ArgumentNullException(nameof(retries));
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
            {
                Status: AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Failed or AgentWorkSessionStatus.Cancelled
            })
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
            return 1 + await PollAsync(store, graph, run, nodeRun with
            {
                Status = DevWorkflowNodeRunStatus.Running
            }, nodeRuns, cancellationToken).ConfigureAwait(false);
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
        catch (DevWorkflowPolicyException exception)
        {
            // GRAPH-C4-2's runtime half. NOT Configuration: nothing is misconfigured — the node's agent may do more
            // than the definition admits to, and only a person can say whether that is meant. Policy blocks for a
            // human rather than failing the run, and a retry would produce the same answer.
            return written + await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Policy, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!await TryDriveAsync(session, graph, node, cancellationToken).ConfigureAwait(false))
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
        DevWorkflowGraph graph,
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
                return await SucceedAsync(store, graph, run, nodeRun, nodeRuns, session, cancellationToken).ConfigureAwait(false);

            case AgentWorkSessionStatus.Failed:

                // GRAPH-C4-2's runtime half, read back — from the RECORD of the refusal, never re-decided here. The
                // send refuses a turn whose own tool offer widened past what the node declared — a definition edited
                // between two steps, or deleted so the turn falls back to the default persona — and all the session can
                // do about it is stop. The row it wrote is what turns that stop into this lane's own refusal, carrying
                // the class and the sentence rather than "the agent's work session failed".
                //
                // Asking the guard again instead would answer about the definition as it stands NOW: an operator who
                // restored or narrowed it between the refusal and this poll would send the node run down the retryable
                // provider-failure path, losing the Policy class for a refusal that really happened. A historical cause
                // is read, not recomputed. The node's declaration is still consulted first, off the run's PINNED graph,
                // so an ordinary provider failure costs nothing but a dictionary miss.
                if (DeclarationRequired(graph, graph.Nodes.GetValueOrDefault(nodeRun.NodeKey))
                    && await ReadWriteGateRefusalAsync(sessionId, cancellationToken).ConfigureAwait(false) is { } refusal)
                {
                    return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Policy, refusal, cancellationToken).ConfigureAwait(false);
                }

                // The retry policy's answer, not this lane's: a provider failure is retryable, and whether THIS one is
                // re-attempted depends on the node's cap, the run's budget and whether the node routes its failures
                // upstream — none of which is the session's business.
                return await _retries.SettleFailureAsync(store,
                                         graph,
                                         run,
                                         nodeRun,
                                         nodeRuns,
                                         new DevWorkflowFailure(DevWorkflowFailureClasses.ProviderError,
                                             "The agent's work session failed.",
                                             FailureOutput(nodeRun, session, DevWorkflowFailureClasses.ProviderError)),
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
                    : await ResumeAsync(store, run, graph, graph.Nodes.GetValueOrDefault(nodeRun.NodeKey), nodeRun, session, cancellationToken).ConfigureAwait(false);

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
        await EnsureDeclaredWhatItCanWriteAsync(graph, node, agentDefinitionId, cancellationToken).ConfigureAwait(false);
        var objective = await ComposeObjectiveAsync(store, graph, run, node, nodeRun, cancellationToken).ConfigureAwait(false);
        var created = await _sessions.CreateAsync(node.Label, objective, agentDefinitionId, RuntimeOf(graph, node), cancellationToken).ConfigureAwait(false);

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
    ///     <para>
    ///         The graph node's model and effort travel on EVERY drive, not only the create: the work-session layer
    ///         holds them for the run it is driving rather than storing them, so a resume after a restart is what puts
    ///         them back. The run's pinned graph is the durable copy, which is also why a definition edited mid-run
    ///         cannot change what a running node dispatches on.
    ///     </para>
    /// </summary>
    private async Task<bool> TryDriveAsync(WorkSessionDetail session, DevWorkflowGraph graph, DevWorkflowGraphNode? node, CancellationToken cancellationToken)
    {
        try
        {
            var runtime = RuntimeOf(graph, node);
            _ = session.Status switch
            {
                AgentWorkSessionStatus.Draft => await _sessions.StartAsync(session.Id, runtime, cancellationToken).ConfigureAwait(false),
                AgentWorkSessionStatus.Paused or AgentWorkSessionStatus.Interrupted => await _sessions.ResumeAsync(session.Id, runtime, cancellationToken).ConfigureAwait(false),

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

    /// <summary>
    ///     What the node authored for its session to run on, plus whether that session's turns have to keep proving
    ///     they were declared. Null when the node authored no pin and needs no proof, and the bound agent's own
    ///     configuration is the whole answer. A node run whose key is not in the run's graph — nothing produces one, but
    ///     the lookup is a dictionary miss away — reads as "no override" rather than failing a resume over a label.
    ///     <para>
    ///         Re-supplied on every start and resume, which is what makes the refusal flag the right place for
    ///         <c>GRAPH-C4-2</c>'s per-turn half: it is read off the run's PINNED graph each time, so nothing an
    ///         operator edits mid-run can widen what a running session is allowed to be offered.
    ///     </para>
    /// </summary>
    private static WorkSessionRuntimeOverride? RuntimeOf(DevWorkflowGraph graph, DevWorkflowGraphNode? node)
    {
        var runtime = new WorkSessionRuntimeOverride(node?.ModelProfile, node?.ReasoningEffort, DeclarationRequired(graph, node));
        return runtime.IsEmpty ? null : runtime;
    }

    /// <summary>
    ///     <c>GRAPH-C4-2</c>'s runtime half at the seam where the session is CREATED: an Agent node whose bound agent
    ///     will really be offered a tool that writes or runs commands has to have SAID so, or the template has to have
    ///     waived the rule once and in writing.
    ///     <para>
    ///         The structural half alone would be inert by construction — it can only bite on an apply node, which Y3
    ///         already gates, and on a declaration an author volunteered. What a node may actually do is decided when
    ///         the binding is resolved, so the question is asked there.
    ///     </para>
    ///     <para>
    ///         This one is the EARLY answer, not the whole of it. It refuses before a session exists, where the refusal
    ///         costs nothing and reads as configuration rather than as a stopped session — but the thing it judges is
    ///         mutable, so <see cref="ArmedFor" /> hands the same question to every turn the session then takes.
    ///     </para>
    ///     <para>
    ///         A node that already declares <c>WriteExecute</c> is not asked at all: its declaration has dragged it
    ///         into the structural rule's gate requirement at save, which is the stronger answer and the earlier one.
    ///     </para>
    /// </summary>
    private async Task EnsureDeclaredWhatItCanWriteAsync(DevWorkflowGraph graph, DevWorkflowGraphNode node, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        if (!DeclarationRequired(graph, node))
        {
            return;
        }

        if (await _writes.InspectAsync(agentDefinitionId, node.ModelProfile, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            throw new DevWorkflowPolicyException(refusal);
        }
    }

    /// <summary>
    ///     Whether this node has to declare what its agent can write: it declares no <c>WriteExecute</c> of its own and
    ///     its template waives nothing. Both halves are read off the run's pinned graph, so an operator editing the
    ///     definition cannot move them mid-run.
    /// </summary>
    private static bool DeclarationRequired(DevWorkflowGraph graph, DevWorkflowGraphNode? node) =>
        node is { NodeType: DevWorkflowNodeType.Agent }
        && !graph.AllowUngatedWrites
        && !DevWorkflowGraph.Effects(node).Contains(DevWorkflowNodeEffect.WriteExecute);

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
    ///     <para>
    ///         Each upstream artifact is rendered with its CONTENTS, not merely its name and id. A reference alone is
    ///         useless to a node that has no way to dereference it: the seeded plan node is told to turn research.md
    ///         into a plan, and handed only "Report 'research.md' (version 1, id …)" it would invent one. The bytes
    ///         travel in the objective because that is the one channel the agent lane already has.
    ///     </para>
    /// </summary>
    private async Task<string> ComposeObjectiveAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        var objective = new StringBuilder();
        _ = objective.AppendLine(node.Instructions is { Length: > 0 } instructions ? instructions : $"Carry out the '{node.Label}' step of this development workflow.");

        // A node whose template subtree carries a DevTask is writing work for the implementation lane, so it is told
        // what that lane can and cannot do — in code, because it is the lane's contract rather than one template's
        // strategy. Gated on the SAME predicate the materializer refuses a task package by: a template of Agent and
        // Tool nodes produces no coder attempt, and telling its decomposition that every task must export a patch and
        // add a new test file would bind it to rules nothing there enforces.
        // Uncapped like the instructions and counted the same way: it lands before the policy phase reads
        // objective.Length, so policy gets the room this leaves rather than overrunning the limit behind it.
        if (node.Materialization is { } materialization && graph.TemplateSubtreeHasDevTask(materialization))
        {
            _ = objective.AppendLine().AppendLine(DevWorkflowDecompositionContract.Text);
        }

        // The scoped rule sets, between the node's own instructions and what was asked, per §5.6.1a. Their text counts
        // against the same budget everything else does: policy that pushed the objective over the limit would crowd out
        // the request it is supposed to govern.
        //
        // Read straight from what the node run RECORDED — id, name, hash and the text itself — and never from the rule
        // set as it stands now. Editing or deleting a rule set mid-run is allowed, so a dispatch-time read would hand
        // the agent a document the audit does not name, or nothing at all.
        // Rendered BEFORE the policy phase though it is appended after it, because it is uncapped: instructions and the
        // operator's request are what a node cannot do without, so policy gets the room they leave rather than the room
        // it would like. Without this the bounded phase would fill the budget and the unbounded one would then push
        // straight past it.
        var inputSection = new StringBuilder();
        if (ReadInput(nodeRun.InputJson) is { Count: > 0 } input)
        {
            _ = inputSection.AppendLine().AppendLine("## What was asked");
            foreach (var (name, value) in input)
            {
                _ = inputSection.AppendLine(CultureInfo.InvariantCulture, $"- {name}: {value}");
            }
        }

        // The fair share, the visible truncation marker and the dropped-with-a-warning are DevWorkflowPolicyText's,
        // shared with the DevTask lane so the two cannot drift into rendering the same recorded rule sets differently.
        var policyCeiling = MaxObjectiveCharacters - inputSection.Length;
        _ = objective.Append(DevWorkflowPolicyText.Render(DevWorkflowRulePolicyResolver.Read(nodeRun.PolicyResolutionJson),
            policyCeiling,
            objective.Length,
            nodeRun.Id,
            _logger));

        _ = objective.Append(inputSection);

        var upstream = await DevWorkflowUpstreamArtifacts.RecordAsync(store, graph, run, nodeRun, cancellationToken).ConfigureAwait(false);

        // What did NOT arrive belongs in the same section as what did. An All join now carries on past a leaf a person
        // skipped, so this node can be handed four implementations where the fan-out was five wide — and with nothing
        // saying so it would judge the four as if they were the whole job. Named here rather than left to the absence.
        var skipped = await DevWorkflowUpstreamArtifacts.SkippedAsync(store, graph, run.Id, nodeRun.NodeKey, cancellationToken).ConfigureAwait(false);
        var section = $"{Environment.NewLine}## What the steps before you produced{Environment.NewLine}";
        if ((upstream.Count > 0 || skipped.Count > 0) && objective.Length + section.Length <= MaxObjectiveCharacters)
        {
            _ = objective.Append(section);

            // Composed FIRST though they are appended last. One line per skipped step, carrying the reason the row
            // kept — which for an operator's decision is the operator's own words — with the heading folded into the
            // first line, so a budget that runs out leaves no heading promising steps it could not name.
            var lines = skipped.Select(static skip => string.Create(CultureInfo.InvariantCulture,
                                    $"- '{skip.NodeKey}' was skipped{(skip.TerminalReason is { Length: > 0 } reason ? $": {reason}" : ".")}{Environment.NewLine}"))
                               .ToList();
            if (lines.Count > 0)
            {
                lines[0] = $"{Environment.NewLine}### Skipped steps{Environment.NewLine}{lines[0]}";
            }

            // Their room is then held BACK from what the artifacts apportion. The share below hands the bodies every
            // remaining character, so one long document would truncate to the ceiling itself and leave the lines
            // nothing to fit into — the node would again be told nothing about the work that did not happen, and an
            // absence cannot be read. Reserving is cheap: a line is a node key and a reason bounded at a kilobyte.
            // Capped at half the room so the reserve cannot invert the problem, a wide fan-out of skipped branches
            // crowding out the branches that DID produce.
            var reserve = Math.Min(lines.Sum(static line => line.Length), (MaxObjectiveCharacters - objective.Length) / 2);
            var artifactCeiling = MaxObjectiveCharacters - reserve;
            for (var index = 0; index < upstream.Count; index++)
            {
                var artifact = upstream[index];
                var header = string.Create(CultureInfo.InvariantCulture,
                    $"{Environment.NewLine}### {artifact.Kind} '{artifact.Name}' (version {artifact.Version}, id {artifact.Id}){Environment.NewLine}");

                // Everything still to be written shares the room left equally — the room left BELOW the reserve, not
                // below the limit — so a long first document cannot crowd out the ones after it and a short one hands
                // its slack on. The share has to cover this artifact's own header and marker as well as its body,
                // which is why both come off it before the body is asked for.
                var share = (artifactCeiling - objective.Length) / (upstream.Count - index);
                var body = await RenderArtifactAsync(run.Id, artifact, share - header.Length - DevWorkflowPolicyText.TruncationMarkerReserve, cancellationToken).ConfigureAwait(false);

                // The bound is enforced HERE, on the FINISHED block, with the header, the body, whichever marker was
                // rendered and the newlines all counted. Nothing reaches the objective except through this check, so
                // no reference-only line, marker or rounding can push it past the reserved ceiling. A block that will
                // not fit falls back to its header alone — the agent still learns the artifact exists, which is the
                // half that matters most — and one with no room even for that is dropped rather than allowed to
                // overrun.
                foreach (var candidate in new[]
                         {
                             header + body + Environment.NewLine,
                             header
                         })
                {
                    if (objective.Length + candidate.Length <= artifactCeiling)
                    {
                        _ = objective.Append(candidate);
                        break;
                    }
                }
            }

            // Last, and under the SAME overall bound as everything else rather than under the reserve: whatever the
            // artifacts left unspent is the list's to use, and a line about work that did not happen still must not
            // push the objective past the limit.
            foreach (var line in lines)
            {
                if (objective.Length + line.Length > MaxObjectiveCharacters)
                {
                    break;
                }

                _ = objective.Append(line);
            }
        }

        return objective.ToString().TrimEnd();
    }

    /// <summary>
    ///     One upstream artifact's contents, or the line that says why they are not here.
    ///     <para>
    ///         Text only, decided from the DECLARED media type rather than by sniffing bytes, and only once the blob
    ///         store has verified the artifact row's own digest and size. An artifact whose bytes no longer match what
    ///         produced them is handed over as a reference and a warning: silently injecting unverified content is how
    ///         a tampered file would end up being reasoned about as if it were the research.
    ///     </para>
    /// </summary>
    private async Task<string> RenderArtifactAsync(Guid runId, DevWorkflowArtifactSnapshot artifact, int budget, CancellationToken cancellationToken)
    {
        if (!ArtifactMediaTypes.IsText(artifact.MediaType))
        {
            return $"(Its bytes are {artifact.MediaType}, not text, so only this reference is given.)";
        }

        if (budget <= 0)
        {
            return "(The objective had no room left for its contents, so only this reference is given.)";
        }

        if (artifact.SizeBytes > MaxInjectableArtifactBytes)
        {
            // Gated on the row's recorded size BEFORE the read, because reading is what costs: the blob store
            // materialises the whole blob and the decode allocates a UTF-16 copy of it, and an artifact may be as
            // large as DevWorkflowOptions allows — so a fan-in of them would allocate hundreds of megabytes to keep a
            // few thousand characters. The trade is that a huge document loses even its prefix, which is the right way
            // round: the first few thousand characters of a 64 MiB file were never grounding, and the marker says so.
            return $"(It is {artifact.SizeBytes} bytes, too large to include here, so only this reference is given.)";
        }

        var read = await _blobs.ReadAsync(runId, artifact.Id, artifact.ContentSha256, artifact.SizeBytes, cancellationToken).ConfigureAwait(false);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            _logger.LogWarning("Development workflow artifact {ArtifactId} of run {RunId} was not injected into an objective: {Status}.",
                artifact.Id,
                runId,
                read.Status);
            return $"(Its stored bytes did not verify ({read.Status}), so only this reference is given. Do not assume what it said.)";
        }

        var content = Encoding.UTF8.GetString(read.Content.Span);
        if (content.Length <= budget)
        {
            return content;
        }

        var cut = DevWorkflowPolicyText.CutAt(content, budget);
        return $"{content[..cut]}{Environment.NewLine}(Truncated: the first {cut} of {content.Length} characters.)";
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
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        WorkSessionDetail session,
        CancellationToken cancellationToken)
    {
        // Evidence first, status last — the same order the work-session loop uses one level down. A crash in that
        // window re-derives the same answer, because the promotion is keyed and the poll runs again.
        //
        // A decomposing node declares the artifact kind it produces, and that declaration is what the promotion needs:
        // the session's own enum cannot say "task package", so without it the document this node's whole purpose is to
        // hand downstream would land as an ordinary report and the materialization would find nothing.
        var declaredKind = graph.Nodes.GetValueOrDefault(nodeRun.NodeKey)?.Materialization?.ArtifactKind;
        var promoted = await _promotion.PromoteAsync(run, nodeRun, session.Id, declaredKind, cancellationToken).ConfigureAwait(false);
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
        DevWorkflowGraph graph,
        DevWorkflowGraphNode? node,
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

        if (!_sessions.HasCapacity || !await TryDriveAsync(session, graph, node, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    ///     The sentence the write-declaration gate stopped this session with, or <see langword="null" /> when nothing
    ///     stopped it that way.
    ///     <para>
    ///         Read off the step row the supervisor wrote at the moment of the refusal — the outcome tag is the stable
    ///         code and its detail is the sentence — so the answer is a fact about what happened rather than a verdict
    ///         about what the agent definition currently allows. The LAST such row wins for the same reason a terminal
    ///         reason does: it is the one the session settled on.
    ///     </para>
    /// </summary>
    private async Task<string?> ReadWriteGateRefusalAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var events = await _sessionStore.ListEventsAsync(sessionId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        var refused = events.LastOrDefault(static entry => string.Equals(entry.Outcome, WorkSessionEventTypes.WriteGateOutcome, StringComparison.Ordinal));
        return WorkSessionEventTypes.ReadWriteGateDetail(refused?.DetailJson);
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
