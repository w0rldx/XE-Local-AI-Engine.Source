namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>The agent lane: one work session per agent node-run attempt, driven by the run that owns it.</summary>
/// <remarks>
///     A CLIENT of the work-session machinery, not a fork: the executor, checkpoints, transcript and pause/resume
///     live one level down. This adds an objective from the node's inputs, admission that queues honestly when the
///     one invocation slot is held, a poll turning session status into node-run status, and promotion of the output
///     into the run's audit. Transitions are written inside the dispatcher's serialized tick, keeping the
///     <c>AdvanceOnceAsync</c> invariant true. See docs/wiki/25-dev-workflows.md ("The agent lane").
/// </remarks>
internal sealed class DevWorkflowAgentExecutor
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    /// <summary>
    ///     How the completion event's detail is read.
    /// </summary>
    /// <remarks>
    ///     Case-INSENSITIVE deliberately: the handler writes it with bare defaults (PascalCase), and a later tidy-up
    ///     onto the shared Web options would rename the members to camelCase — after which a case-sensitive read binds
    ///     nothing, every declared unmet objective reads as met, and a session that closed WITHOUT meeting its
    ///     objective reads as a success, with no exception and no log line to find it by.
    /// </remarks>
    private static readonly JsonSerializerOptions CompletionDetailOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>How many blocked plan tasks a terminal reason names before it counts the rest.</summary>
    private const int MaxNamedTasks = 3;

    /// <summary>The read-only coder tools, which a node bound to a Development project serves from that project's repository.</summary>
    internal static readonly string[] RepositoryToolNames =
        [CoderToolDefinition.ListFilesToolName, CoderToolDefinition.ReadFileToolName, CoderToolDefinition.SearchTextToolName];

    private readonly IAgentDefinitionStore _agents;
    private readonly ILogger<DevWorkflowAgentExecutor> _logger;
    private readonly DevWorkflowObjectiveComposer _objectives;
    private readonly DevWorkflowOptions _options;
    private readonly DevWorkflowArtifactPromotion _promotion;
    private readonly DevWorkflowRetryPolicy _retries;
    private readonly IAgentWorkSessionStore _sessionStore;
    private readonly IWorkflowOwnedWorkSessionLifecycle _sessions;
    private readonly WorkSessionToolGate _toolGate;
    private readonly WorkSessionWriteDeclarationGuard _writes;
    private readonly IServiceProvider _services;

    public DevWorkflowAgentExecutor(IWorkflowOwnedWorkSessionLifecycle sessions,
        IAgentWorkSessionStore sessionStore,
        IAgentDefinitionStore agents,
        WorkSessionWriteDeclarationGuard writes,
        WorkSessionToolGate toolGate,
        DevWorkflowArtifactPromotion promotion,
        IDevWorkflowArtifactBlobStore blobs,
        DevWorkflowRetryPolicy retries,
        IOptions<DevWorkflowOptions> options,
        IServiceProvider services,
        ILogger<DevWorkflowAgentExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _toolGate = toolGate ?? throw new ArgumentNullException(nameof(toolGate));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        _retries = retries ?? throw new ArgumentNullException(nameof(retries));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _objectives = new DevWorkflowObjectiveComposer(blobs ?? throw new ArgumentNullException(nameof(blobs)), _logger);
    }

    /// <summary>Admits an eligible agent node run, and answers how many transitions it wrote.</summary>
    /// <remarks>
    ///     The row goes to <c>Queued</c> first, always, even when a slot is free a line later. It costs one event and
    ///     makes the queue honest: three parallel agent nodes on a one-slot node are <c>Running, Queued, Queued</c>,
    ///     and a reader has to be able to see that rather than infer it.
    /// </remarks>
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

        if (await TryReadAttachedAsync(nodeRun, cancellationToken) is { Status: AgentWorkSessionStatus.Completed or AgentWorkSessionStatus.Failed or AgentWorkSessionStatus.Cancelled })
        {
            // The session landed and the host died before the poll wrote what it said. Nothing needs re-running: the row is settled off the session's own answer, exactly what that tick
            // would have written. A retry does not come through here — it releases its session first, precisely so it cannot.
            DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
                {
                    RunId = run.Id,
                    NodeRunId = nodeRun.Id,
                    ExpectedVersion = DevWorkflowVersions.Any,
                    TargetStatus = DevWorkflowNodeRunStatus.Running
                },
                cancellationToken);
            return 1 + await PollAsync(store, graph, run, nodeRun with
            {
                Status = DevWorkflowNodeRunStatus.Running
            }, nodeRuns, cancellationToken);
        }

        var written = 0;
        if (nodeRun.Status == DevWorkflowNodeRunStatus.Pending)
        {
            DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Queued, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
                {
                    RunId = run.Id,
                    NodeRunId = nodeRun.Id,
                    ExpectedVersion = DevWorkflowVersions.Any,
                    TargetStatus = DevWorkflowNodeRunStatus.Queued,
                    QueueReason = DevWorkflowQueueReasons.AwaitingAgentSlot
                },
                cancellationToken);
            written++;
        }

        if (!_sessions.HasCapacity)
        {
            // Queueing, not failure: nothing is wrong, the node's one slot is simply held. No event, no failure class — the row's reason says what it waits for and the next tick asks.
            return written;
        }

        WorkSessionDetail session;
        WorkSessionRuntimeOverride? runtime;
        IReadOnlyList<WorkSessionReadableArtifact> readable;
        try
        {
            // The repository is resolved ONCE per admission, before a new session exists, so a node that cannot serve its reads blocks without one to release.
            var attached = await TryReadAttachedAsync(nodeRun, cancellationToken);
            var agentDefinitionId = attached?.AgentDefinitionId ?? await ResolveAgentAsync(node, cancellationToken);
            var repositoryRoot = await RepositoryRootAsync(nodeRun, agentDefinitionId, cancellationToken);
            session = attached ?? await CreateSessionAsync(store, graph, run, node, nodeRun, agentDefinitionId, cancellationToken);

            // After the create, whose objective composition is what records the consumption the read tool is scoped to.
            readable = await ReadableArtifactsAsync(store, run, nodeRun, cancellationToken);
            runtime = RuntimeOf(graph, node, repositoryRoot, readable);
        }
        catch (WorkSessionValidationException exception)
        {
            // A missing agent, a model that cannot call tools, a node with work sessions switched off. A retry produces the same answer, so it goes straight to a human with the message
            // verbatim — it is already sanitized and already names the fix.
            return written + await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken);
        }
        catch (DevWorkflowValidationException exception)
        {
            return written + await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken);
        }
        catch (DevWorkflowPolicyException exception)
        {
            // GRAPH-C4-2's runtime half. NOT Configuration: nothing is misconfigured — the node's agent may do more than the definition admits to, and only a person can say
            // whether that is meant. Policy blocks for a human rather than failing the run, and a retry would produce the same answer.
            return written + await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Policy, exception.Message, cancellationToken);
        }

        if (await SameModelReviewAsync(node, nodeRun, nodeRuns, session.AgentDefinitionId, readable, cancellationToken) is { } review)
        {
            // Keyed by attempt, so a lost admission race that dispatches again replays this event rather than repeating it.
            _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand
                {
                    RunId = run.Id,
                    ExpectedVersion = DevWorkflowVersions.Any,
                    EventType = DevWorkflowEventTypes.NodeReviewerSameModel,
                    NodeRunId = nodeRun.Id,
                    OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "reviewer-same-model"),
                    DetailJson = JsonSerializer.Serialize(review, JsonOptions)
                },
                cancellationToken);
        }

        if (!await TryDriveAsync(session, runtime, cancellationToken))
        {
            // Lost the admission race between the capacity read and the start. The row stays Queued with its reason and keeps the session it owns, so the next tick starts that one.
            return written;
        }

        DevWorkflowStateMachine.EnsureLegal(DevWorkflowNodeRunStatus.Queued, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                TargetStatus = DevWorkflowNodeRunStatus.Running
            },
            cancellationToken);
        return written + 1;
    }

    /// <summary>
    ///     Reads what the node run's session is doing and settles the row when it has landed, answering how many
    ///     transitions it wrote.
    /// </summary>
    /// <remarks>
    ///     The session status is the only authority: this never remembers what it dispatched, which is why a restart
    ///     costs nothing but the poll that follows it.
    /// </remarks>
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
                cancellationToken);
        }

        var session = await TryReadAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return await BlockAsync(store,
                run,
                nodeRun,
                DevWorkflowFailureClasses.Configuration,
                "The work session this node run was driving no longer exists.",
                cancellationToken);
        }

        switch (session.Status)
        {
            case AgentWorkSessionStatus.Completed:
                return await CompleteAsync(store, graph, run, nodeRun, nodeRuns, session, cancellationToken);

            case AgentWorkSessionStatus.Failed:

                // GRAPH-C4-2 read BACK from the refusal's own row, never recomputed: asking the guard again answers about the definition as it stands now, losing the class.
                // That row carries the class and sentence rather than a generic failure. The declaration is checked first off the PINNED graph, so a plain provider failure costs a miss.
                if (DeclarationRequired(graph, graph.Nodes.GetValueOrDefault(nodeRun.NodeKey))
                    && await ReadWriteGateRefusalAsync(sessionId, cancellationToken) is { } refusal)
                {
                    return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Policy, refusal, cancellationToken);
                }

                // The retry policy's answer, not this lane's: a provider failure is retryable, and whether THIS one is re-attempted depends on the node's cap, the run's budget and
                // whether the node routes its failures upstream — none of which is the session's business.
                return await _retries.SettleFailureAsync(store,
                    graph,
                    run,
                    nodeRun,
                    nodeRuns,
                    new DevWorkflowFailure
                    {
                        FailureClass = DevWorkflowFailureClasses.ProviderError,
                        SanitizedReason = "The agent's work session failed.",
                        OutputJson = FailureOutput(nodeRun, session, DevWorkflowFailureClasses.ProviderError)
                    },
                    cancellationToken);

            case AgentWorkSessionStatus.Cancelled:
                return await SettleAsync(store,
                    run,
                    nodeRun,
                    nodeRuns,
                    DevWorkflowNodeRunStatus.Cancelled,
                    DevWorkflowFailureClasses.Cancelled,
                    "The agent's work session was cancelled.",
                    FailureOutput(nodeRun, session, DevWorkflowFailureClasses.Cancelled),
                    cancellationToken);

            case AgentWorkSessionStatus.Draft or AgentWorkSessionStatus.Paused or AgentWorkSessionStatus.Interrupted:

                // Never while the run is draining: under a pause the session was paused ON PURPOSE a moment ago, and resuming here would undo the operator's command mid-Pausing.
                return run.Status is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling
                    ? 0
                    : await ResumeAsync(store, run, graph, graph.Nodes.GetValueOrDefault(nodeRun.NodeKey), nodeRun, session, cancellationToken);

            default:
                // Running, or parked on a question it asked. Still working; nothing to write.
                return 0;
        }
    }

    /// <summary>
    ///     Asks the session to stop, for whichever drain the run is in.
    /// </summary>
    /// <remarks>
    ///     The row is deliberately NOT settled here: only the session knows where it lands, and the next tick's poll
    ///     writes that rather than this guessing it.
    /// </remarks>
    public async Task StopAsync(Guid sessionId, bool cancel, CancellationToken cancellationToken)
    {
        try
        {
            _ = cancel
                ? await _sessions.CancelAsync(sessionId, cancellationToken)
                : await _sessions.PauseAsync(sessionId, cancellationToken);
        }
        catch (Exception exception) when (exception is WorkSessionInvalidTransitionException or WorkSessionNotFoundException)
        {
            // Already settled, or already gone. Either way there is nothing left to stop and the poll reads the truth.
            _logger.LogDebug(exception, "Work session {SessionId} could not be stopped by its owning run; it has already settled.", sessionId);
        }
    }

    /// <summary>
    ///     A fresh session for this attempt, attached to its node run; the caller reuses one already attached if it can still be driven.
    /// </summary>
    /// <remarks>
    ///     A retry always gets a NEW session — resuming the one that just failed resumes its poisoned context — but a
    ///     session that is merely <c>Draft</c>, <c>Paused</c> or <c>Interrupted</c> is the crash window between the
    ///     attach and the start, or a pause, and reusing it keeps a restart from stranding a conversation nobody will
    ///     ever drive.
    /// </remarks>
    private async Task<WorkSessionDetail> CreateSessionAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid agentDefinitionId,
        CancellationToken cancellationToken)
    {
        await EnsureDeclaredWhatItCanWriteAsync(graph, node, agentDefinitionId, cancellationToken);
        var objective = await _objectives.ComposeAsync(store, graph, run, node, nodeRun, cancellationToken);
        var created = await _sessions.CreateAsync(node.Label, objective, agentDefinitionId, RuntimeOf(graph, node), cancellationToken);

        try
        {
            _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand
                {
                    RunId = run.Id,
                    NodeRunId = nodeRun.Id,
                    ExpectedVersion = DevWorkflowVersions.Any,
                    WorkSessionId = created.Id,
                    OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "attach")
                },
                cancellationToken);
        }
        catch
        {
            // Until the attach commits, NOTHING points at this session: the next tick creates another, a work-item delete cannot find it, and the external lifecycle refuses a
            // workflow-kind session to every other caller. So the create is undone here rather than left for the startup sweep, and the original failure is what propagates.
            await ReleaseUnattachedAsync(store, created.Id);
            throw;
        }

        return created;
    }

    /// <summary>Deletes a session no node run owns, and leaves an owned one alone.</summary>
    /// <remarks>
    ///     Ownership is re-read across ALL node runs rather than assumed from the failure, and both directions matter:
    ///     an attach can commit and still throw on the way back (a cancellation between the commit and the return),
    ///     and an attach can fail precisely BECAUSE another node run already owns that session. Deleting in either
    ///     case would take a transcript out from under a row that points at it. It runs without a cancellation token,
    ///     because this is the cleanup for a call that may itself have been cancelled.
    /// </remarks>
    private async Task ReleaseUnattachedAsync(IDevWorkflowStore store, Guid sessionId)
    {
        try
        {
            if ((await store.ListOwnedWorkSessionIdsAsync(CancellationToken.None)).Contains(sessionId))
            {
                return;
            }

            await _sessions.DeleteAsync(sessionId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Reported, never rethrown: the caller's failure is the one worth surfacing, and a session left here is exactly what the startup sweep is for.
            _logger.LogWarning(exception, "Work session {SessionId} could not be released after its attach failed.", sessionId);
        }
    }

    /// <summary>
    ///     Starts or resumes the session, whichever its status calls for, answering <see langword="false" /> when the
    ///     node refused the admission — which is a queue, not a failure.
    /// </summary>
    /// <remarks>
    ///     The graph node's model and effort travel on EVERY drive, not only the create: the work-session layer holds
    ///     them for the run it is driving rather than storing them, so a resume after a restart is what puts them
    ///     back. The run's pinned graph is the durable copy, which is also why a definition edited mid-run cannot
    ///     change what a running node dispatches on.
    /// </remarks>
    private async Task<bool> TryDriveAsync(WorkSessionDetail session, WorkSessionRuntimeOverride? runtime, CancellationToken cancellationToken)
    {
        try
        {
            _ = session.Status switch
            {
                AgentWorkSessionStatus.Draft => await _sessions.StartAsync(session.Id, runtime, cancellationToken),
                AgentWorkSessionStatus.Paused or AgentWorkSessionStatus.Interrupted => await _sessions.ResumeAsync(session.Id, runtime, cancellationToken),

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
    ///     they were declared, or null when the bound agent's own configuration is the whole answer.
    /// </summary>
    /// <remarks>
    ///     A node run whose key is not in the run's graph — nothing produces one, but the lookup is a dictionary miss
    ///     away — reads as "no override" rather than failing a resume over a label. Re-supplied on every start and
    ///     resume, which makes the refusal flag the right place for <c>GRAPH-C4-2</c>'s per-turn half: it is read off
    ///     the run's PINNED graph each time, so nothing edited mid-run can widen what a running session is offered.
    /// </remarks>
    private static WorkSessionRuntimeOverride? RuntimeOf(DevWorkflowGraph graph,
        DevWorkflowGraphNode? node,
        string? repositoryRoot = null,
        IReadOnlyList<WorkSessionReadableArtifact>? readableArtifacts = null)
    {
        var runtime = new WorkSessionRuntimeOverride
        {
            ModelProfile = node?.ModelProfile,
            ReasoningEffort = node?.ReasoningEffort,
            RefuseUndeclaredWrites = DeclarationRequired(graph, node),
            RepositoryRoot = repositoryRoot,
            OutputSchema = node?.OutputSchema,
            ReadableArtifacts = readableArtifacts ?? []
        };
        return runtime.IsEmpty ? null : runtime;
    }

    /// <summary>What <c>read_upstream_artifact</c> may read in this node run's session: what the node run RECORDED consuming.</summary>
    /// <remarks>
    ///     Read off the consumption rows, never the live graph. Those rows are per node run rather than per attempt, and every attempt
    ///     records the latest version of each lineage it is handed, so the newest consumed version of a lineage is the current attempt's.
    /// </remarks>
    internal static async Task<IReadOnlyList<WorkSessionReadableArtifact>> ReadableArtifactsAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        var consumed = (await store.ListConsumedArtifactIdsAsync(nodeRun.Id, cancellationToken)).ToHashSet();
        if (consumed.Count == 0)
        {
            return [];
        }

        var artifacts = await store.ListArtifactsAsync(run.Id, sinceSequence: 0, cancellationToken);
        return
        [
            .. artifacts.Where(artifact => consumed.Contains(artifact.Id))
                        .GroupBy(static artifact => artifact.LineageId)
                        .Select(static lineage => lineage.MaxBy(static artifact => artifact.Version)!)
                        .OrderBy(static artifact => artifact.Sequence)
                        .Select(artifact => new WorkSessionReadableArtifact
                        {
                            RunId = run.Id,
                            ArtifactId = artifact.Id,
                            Name = artifact.Name,
                            Kind = artifact.Kind.ToString(),
                            Producer = artifact.ProducingNodeKey,
                            Version = artifact.Version,
                            MediaType = artifact.MediaType,
                            ContentSha256 = artifact.ContentSha256,
                            SizeBytes = artifact.SizeBytes
                        })
        ];
    }

    /// <summary>
    ///     <c>GRAPH-C4-2</c>'s runtime half where the session is CREATED: an Agent node whose bound agent will really
    ///     be offered a tool that writes or runs commands has to have SAID so, or its template waived the rule.
    /// </summary>
    /// <remarks>
    ///     The structural half alone is inert by construction — it bites only on an apply node, which the parser
    ///     already gates, and on a declaration an author volunteered — so the question is asked where the binding is
    ///     resolved. This is the EARLY answer, refusing before a session exists where it costs nothing and reads as
    ///     configuration; what it judges is mutable, so the same question is handed to every turn the session takes.
    ///     A node already declaring <c>WriteExecute</c> is not asked: save time gated it, which is the stronger answer.
    /// </remarks>
    private async Task EnsureDeclaredWhatItCanWriteAsync(DevWorkflowGraph graph, DevWorkflowGraphNode node, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        if (!DeclarationRequired(graph, node))
        {
            return;
        }

        if (await _writes.InspectAsync(agentDefinitionId, node.ModelProfile, cancellationToken) is { } refusal)
        {
            throw new DevWorkflowPolicyException(refusal);
        }
    }

    /// <summary>
    ///     Whether this node has to declare what its agent can write: it declares no <c>WriteExecute</c> of its own
    ///     and its template waives nothing.
    /// </summary>
    /// <remarks>
    ///     Both halves are read off the run's pinned graph, so an operator editing the definition cannot move them
    ///     mid-run.
    /// </remarks>
    private static bool DeclarationRequired(DevWorkflowGraph graph, DevWorkflowGraphNode? node) =>
        node is { NodeType: DevWorkflowNodeType.Agent }
        && !graph.AllowUngatedWrites
        && !DevWorkflowGraph.Effects(node).Contains(DevWorkflowNodeEffect.WriteExecute);

    /// <summary>
    ///     The repository root this node's coder reads serve, when its agent is offered them; null when it is not.
    /// </summary>
    /// <remarks>
    ///     Refused rather than left to the session when the reads would have nothing to serve: the agent would search an empty workspace,
    ///     declare its objective unmet, and a Retry would end the same way. The refusal names the tools and what each answer does.
    /// </remarks>
    private async Task<string?> RepositoryRootAsync(DevWorkflowNodeRunSnapshot nodeRun, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        if (await _agents.GetByIdAsync(agentDefinitionId, cancellationToken) is not { } agent
            || !agent.AllowedToolNames.Any(name => RepositoryToolNames.Contains(name, StringComparer.Ordinal)))
        {
            return null;
        }

        if (nodeRun.DevelopmentProjectId is not { } projectId)
        {
            throw new DevWorkflowValidationException(MissingRepository(nodeRun.NodeKey, "this run's work item names no Development project"));
        }

        if (_services.GetService<IDevelopmentRepositoryBindingService>() is not { } bindings)
        {
            throw new DevWorkflowValidationException(MissingRepository(nodeRun.NodeKey, "Development Mode is off on this node"));
        }

        try
        {
            return (await bindings.ResolveProjectAsync(projectId, cancellationToken)).RepositoryRoot;
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException or DevelopmentNotFoundException or SelectedFolderValidationException
                                              or IOException or UnauthorizedAccessException)
        {
            // Never the exception's own text: a binding failure can name the host path, and this sentence is shown to whoever reads the run.
            throw new DevWorkflowValidationException(MissingRepository(nodeRun.NodeKey, "its Development project's repository cannot be resolved (reconnect it on the Development page)"), exception);
        }
    }

    private static string MissingRepository(string nodeKey, string why) =>
        $"Agent node '{nodeKey}' runs an agent that reads the repository with {string.Join(", ", RepositoryToolNames)}, and {why}. "
        + "Retry ends the same way until that is fixed; Skip ends this node without its output, which skips every node that depends on it.";

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

        var seeded = await _agents.GetBySeedSlugAsync(slug, cancellationToken);
        return seeded?.Id ?? throw new DevWorkflowValidationException($"Agent node '{node.NodeKey}' binds the seeded agent '{slug}', which this node does not have.");
    }

    /// <summary>What a completed session actually produced, mapped to the node run's fate.</summary>
    /// <remarks>
    ///     A session completes for two reasons its status cannot tell apart: the objective was met, or the step could
    ///     not meet it and closed anyway because nothing else lets a step end. Reading every completion as a success
    ///     lets a verify node that wrote it had NOT signed the work off still go green, so two honest signals are read
    ///     instead — a plan task moved to <c>Blocked</c>, and an <c>objectiveMet:false</c> declared on completion.
    ///     Either stands the row down for the intervention gate. No kind check: every session here is a workflow one.
    /// </remarks>
    private async Task<int> CompleteAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        WorkSessionDetail session,
        CancellationToken cancellationToken)
    {
        var completion = await ReadCompletionAsync(session.Id, cancellationToken);
        var outputSchema = graph.Nodes.GetValueOrDefault(nodeRun.NodeKey)?.OutputSchema;
        var coverage = await ReadCoverageAsync(store, run, nodeRun, session.Id, cancellationToken);
        // An unmetOn answer first: it names what was refused, which a bare objectiveMet:false declared beside it does not.
        var unmet = UnmetOutput(outputSchema, completion)
                    ?? await UnmetObjectiveAsync(session.Id, completion, cancellationToken)
                    ?? OutputViolation(outputSchema, completion)
                    ?? UnreadPatch(coverage);
        if (unmet is null)
        {
            return await SucceedAsync(store, graph, run, nodeRun, nodeRuns, session, outputSchema is null ? null : completion?.Output, coverage, cancellationToken);
        }

        // Promoted exactly as a success would be, because the STATUS carries the honesty and nothing downstream reads a Blocked row as a deliverable. Otherwise the operator choosing
        // Retry, Skip or Abandon decides on the terminal reason alone — and a Retry clears the session pointer, so the run's artifact table is the only place these survive the decision.
        var declaredKind = graph.Nodes.GetValueOrDefault(nodeRun.NodeKey)?.Materialization?.ArtifactKind;
        _ = await _promotion.PromoteAsync(run, nodeRun, session.Id, declaredKind, cancellationToken);
        // The coverage is bounded first and the rest gets what is left, so a long model-authored reason cannot push the Read lines out.
        var reads = coverage.Count == 0 ? string.Empty : Clipped($" Read: {string.Join("; ", coverage.Select(static entry => entry.Line))}.", DevWorkflowStateMachine.MaxNodeTerminalReason / 2);
        var reason = DevWorkflowStateMachine.Bounded(unmet, DevWorkflowStateMachine.MaxNodeTerminalReason - reads.Length) + reads;
        return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.ObjectiveNotMet, reason, cancellationToken);
    }

    /// <summary>How much of each readable text artifact the session read through <c>read_upstream_artifact</c>, off its own <c>ArtifactRead</c> events.</summary>
    /// <remarks>
    ///     The events, not the model's account: a verify that wrote "read in full" in its report is held to the pages it was served. A Retry
    ///     drives a new session, so its coverage starts from zero and the new attempt has to read again.
    /// </remarks>
    private async Task<IReadOnlyList<ArtifactCoverage>> ReadCoverageAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var readable = (await ReadableArtifactsAsync(store, run, nodeRun, cancellationToken)).Where(static artifact => ArtifactMediaTypes.IsText(artifact.MediaType)).ToList();
        if (readable.Count == 0)
        {
            return [];
        }

        var pages = (await _sessionStore.ListEventsAsync(sessionId, sinceSequence: 0, cancellationToken))
                    .Where(static entry => string.Equals(entry.EventType, WorkSessionEventTypes.ArtifactRead, StringComparison.Ordinal))
                    .Select(entry => ReadPage(sessionId, entry.DetailJson))
                    .OfType<WorkSessionArtifactReadDetail>()
                    .ToList();
        return [.. readable.Select(artifact => ArtifactCoverage.Of(artifact, pages.Where(page => page.ArtifactId == artifact.ArtifactId)))];
    }

    private WorkSessionArtifactReadDetail? ReadPage(Guid sessionId, string? detailJson)
    {
        try
        {
            return detailJson is null ? null : JsonSerializer.Deserialize<WorkSessionArtifactReadDetail>(detailJson, CompletionDetailOptions);
        }
        catch (JsonException exception)
        {
            // A page nobody can read counts as unread: the safe direction, since it can only make a patch read as not covered.
            _logger.LogWarning(exception, "Work session {SessionId} recorded an unreadable artifact-read detail.", sessionId);
            return null;
        }
    }

    /// <summary>The warning an Agent node handed a patch earns by running on the model that produced it, or null when it does not or a side is unknown.</summary>
    /// <remarks>
    ///     Never a block: a model judging its own work is a weaker review, not a wrong one. The reviewer's model is the one the session's every step
    ///     is judged on: node pin, bound definition, node default. A patch's is the coder attempt its producer recorded, else the project's coder model.
    /// </remarks>
    private async Task<SameModelReview?> SameModelReviewAsync(DevWorkflowGraphNode? node,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        Guid agentDefinitionId,
        IReadOnlyList<WorkSessionReadableArtifact> readable,
        CancellationToken cancellationToken)
    {
        var patches = readable.Where(static artifact => string.Equals(artifact.Kind, nameof(DevWorkflowArtifactKind.Patch), StringComparison.Ordinal)).ToList();
        if (patches.Count == 0)
        {
            return null;
        }

        // The step loop's own resolution, so the default model a seeded node runs on counts; a deleted definition resolves to nothing.
        if (Pinned((await _toolGate.InspectAllowListAsync(agentDefinitionId, node?.ModelProfile, cancellationToken)).EffectiveModel) is not { } reviewer)
        {
            return null;
        }

        var matched = new List<string>();
        foreach (var patch in patches)
        {
            var producer = nodeRuns.FirstOrDefault(row => string.Equals(row.NodeKey, patch.Producer, StringComparison.Ordinal));
            var coder = RecordedPatchModel(producer?.OutputJson) ?? await ProjectCoderModelAsync(producer?.DevelopmentProjectId ?? nodeRun.DevelopmentProjectId, cancellationToken);
            if (string.Equals(coder, reviewer, StringComparison.Ordinal))
            {
                matched.Add($"'{patch.Name}' from '{patch.Producer}'");
            }
        }

        return matched.Count == 0
            ? null
            : new SameModelReview
            {
                Attempt = nodeRun.Attempt,
                ModelId = reviewer,
                Patches = matched,
                Warning = $"This node reviews {string.Join(", ", matched)} on '{reviewer}', the model that produced it, so its review is not independent of the work it judges."
            };
    }

    private static string? Pinned(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : model;

    /// <summary>The coder model a validation node recorded for the patch it published, off its output's <c>patchModelId</c>.</summary>
    private static string? RecordedPatchModel(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("patchModelId", out var model)
                   && model.ValueKind == JsonValueKind.String
                ? Pinned(model.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ProjectCoderModelAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        if (projectId is not { } id || _services.GetService<IDevelopmentStore>() is not { } development)
        {
            return null;
        }

        try
        {
            return Pinned((await development.GetProjectAsync(id, cancellationToken)).CoderModelId);
        }
        catch (DevelopmentNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Why a completion is not a success because a patch it was handed was not read to its end, or null when every one was.</summary>
    /// <remarks>
    ///     An Agent node handed a patch must read all of it: generic, and today only verify is handed patches. Engine-checked because a model
    ///     judging the header of a diff and calling it reviewed is exactly what the objective text alone failed to prevent.
    /// </remarks>
    private static string? UnreadPatch(IReadOnlyList<ArtifactCoverage> coverage) =>
        coverage.FirstOrDefault(static entry => string.Equals(entry.Artifact.Kind, nameof(DevWorkflowArtifactKind.Patch), StringComparison.Ordinal) && !entry.Complete) is { } gap
            ? DevWorkflowStateMachine.Bounded($"The agent's work session completed without reading the whole of the patch '{gap.Artifact.Name}' from '{gap.Artifact.Producer}': "
                                              + $"{gap.Line}. An Agent node handed a patch must read all of it with read_upstream_artifact.",
                DevWorkflowStateMachine.MaxNodeTerminalReason)
            : null;

    /// <summary>
    ///     Why this completion is not a success, or <see langword="null" /> when it is one.
    /// </summary>
    /// <remarks>
    ///     The blocked plan is read first because it is what the step prompt asks a stuck session for by name, and it
    ///     says WHICH piece of work stalled — which the declaration alone does not.
    /// </remarks>
    private async Task<string?> UnmetObjectiveAsync(Guid sessionId, WorkSessionCompletionDetail? completion, CancellationToken cancellationToken)
    {
        // The store's own order, not re-sorted: ListTasksAsync orders by CreatedStep so a task an update re-stamped does not jump the page, and re-sorting here would name the three
        // least recently touched tasks instead.
        var blocked = (await _sessionStore.ListTasksAsync(sessionId, sinceSequence: 0, cancellationToken))
                      .Where(static task => task.Status == AgentWorkSessionTaskStatus.Blocked)
                      .ToList();
        if (blocked.Count == 0)
        {
            return completion is { ObjectiveMet: false } declared
                ? DevWorkflowStateMachine.Bounded($"The agent's work session declared the objective NOT met: {declared.Summary}", DevWorkflowStateMachine.MaxNodeTerminalReason)
                : null;
        }

        var named = string.Join("; ",
            blocked.Take(MaxNamedTasks)
                   .Select(static task => $"'{task.Title}' ({(string.IsNullOrWhiteSpace(task.BlockedReason) ? "the session recorded no reason" : task.BlockedReason)})"));
        var rest = blocked.Count > MaxNamedTasks ? $", and {blocked.Count - MaxNamedTasks} more" : string.Empty;
        return DevWorkflowStateMachine.Bounded($"The agent's work session completed with its plan still blocked: {named}{rest}.",
            DevWorkflowStateMachine.MaxNodeTerminalReason);
    }

    /// <summary>
    ///     The last completion request the session recorded: its <c>objectiveMet:false</c> declaration and its output.
    /// </summary>
    /// <remarks>
    ///     Read off the completion EVENT, not the session row, which keeps only a status; the event is the same row
    ///     the work-session supervisor reads to close the session. A completion carrying no member reads as met, so
    ///     an upgrade cannot retroactively block a node run that already finished. The LAST completion request
    ///     answers, pairing with the FIRST winning inside one step (the operation id is derived from the step, so a
    ///     repeat replays through dedupe) — a session that kept working after declaring is judged on what it said last.
    /// </remarks>
    private async Task<WorkSessionCompletionDetail?> ReadCompletionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // Targeted, not the whole log: reading and decrypting every event the session wrote to keep one row scales with the session.
        var completion = await _sessionStore.FindLatestEventAsync(sessionId, WorkSessionEventTypes.CompletionRequested, cancellationToken);
        if (completion?.DetailJson is not { Length: > 0 } detail)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkSessionCompletionDetail>(detail, CompletionDetailOptions);
        }
        catch (JsonException exception)
        {
            // Unreadable detail is not evidence of a failure: the supervisor already completed the session on it, and inventing a block from a parse error would strand a node run.
            _logger.LogWarning(exception, "Work session {SessionId} recorded an unreadable completion detail.", sessionId);
            return null;
        }
    }

    /// <summary>Why a node with an output contract cannot route on what its session handed in, or null when it can or has no contract.</summary>
    /// <remarks>
    ///     The handler already refuses a completion without a valid output, so this should be unreachable; it is the defense in depth
    ///     that keeps an edge from routing on an answer nobody checked. An unreadable completion has no output either, so it stands down here.
    /// </remarks>
    private static string? OutputViolation(WorkSessionOutputSchema? schema, WorkSessionCompletionDetail? completion) =>
        schema?.Violation(completion?.Output) is { } violation
            ? DevWorkflowStateMachine.Bounded($"The agent's work session completed without the output its node routes on: {violation}", DevWorkflowStateMachine.MaxNodeTerminalReason)
            : null;

    /// <summary>Why a met completion is not one after all: its output answered a value its node's contract lists in <c>unmetOn</c>.</summary>
    /// <remarks>
    ///     The model said "met" and "refused" in one call, and the answer is the more specific of the two. Every other string member rides
    ///     along, since a template that declares <c>unmetOn</c> pairs it with a member naming what was refused.
    /// </remarks>
    private static string? UnmetOutput(WorkSessionOutputSchema? schema, WorkSessionCompletionDetail? completion)
    {
        if (schema is null || completion?.Output is not { ValueKind: JsonValueKind.Object } output)
        {
            return null;
        }

        var unmet = schema.Properties.FirstOrDefault(property => property.UnmetOn is { } values
                                                                 && output.TryGetProperty(property.Name, out var value)
                                                                 && value.ValueKind == JsonValueKind.String
                                                                 && values.Contains(value.GetString(), StringComparer.Ordinal));
        if (unmet is null)
        {
            return null;
        }

        var companions = string.Concat(schema.Properties
                                             .Where(property => property != unmet && property.Type == WorkSessionOutputType.String)
                                             .Select(property => output.TryGetProperty(property.Name, out var value) && value.ValueKind == JsonValueKind.String
                                                 ? $" {property.Name}: {value.GetString()}"
                                                 : null));
        // The verdict sentence is short and fixed; the companions are model-authored, so they are the part that gets clipped.
        var verdict = $"The agent's work session answered output.{unmet.Name} {output.GetProperty(unmet.Name).GetRawText()}, which this node's output contract treats as "
                      + "the objective NOT met.";
        return verdict + Clipped(companions, DevWorkflowStateMachine.MaxNodeTerminalReason / 2 - verdict.Length);
    }

    /// <summary>At most <paramref name="max" /> characters of <paramref name="text" />, ending in an ellipsis when it was cut.</summary>
    private static string Clipped(string text, int max) =>
        text.Length <= max ? text : DevWorkflowStateMachine.Bounded(text, Math.Max(0, max - 1)) + "…";

    private async Task<int> SucceedAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        WorkSessionDetail session,
        JsonElement? sessionOutput,
        IReadOnlyList<ArtifactCoverage> coverage,
        CancellationToken cancellationToken)
    {
        // Evidence first, status last — the order the work-session loop uses one level down; a crash in that window re-derives the same answer, the promotion being keyed. A decomposing
        // node declares its artifact kind because the session's enum cannot say "task package": without it the task package lands as a report and the materialization finds nothing.
        var declaredKind = graph.Nodes.GetValueOrDefault(nodeRun.NodeKey)?.Materialization?.ArtifactKind;
        var promoted = await _promotion.PromoteAsync(run, nodeRun, session.Id, declaredKind, cancellationToken);
        var findings = await _sessionStore.ListFindingsAsync(session.Id, sinceSequence: 0, cancellationToken);
        var review = await SameModelReviewAsync(graph.Nodes.GetValueOrDefault(nodeRun.NodeKey),
            nodeRun,
            nodeRuns,
            session.AgentDefinitionId,
            await ReadableArtifactsAsync(store, run, nodeRun, cancellationToken),
            cancellationToken);
        var output = JsonSerializer.Serialize(new AgentOutput
            {
                Status = DevWorkflowNodeOutputStatuses.Succeeded,
                Attempt = nodeRun.Attempt,
                FailureClass = null,
                SessionStatus = JsonNamingPolicy.CamelCase.ConvertName(session.Status.ToString()),
                SessionResumes = nodeRun.SessionResumes,
                ArtifactCount = promoted,
                Findings = findings.Where(static finding => !finding.Superseded)
                                   .GroupBy(static finding => finding.Kind)
                                   .ToDictionary(static group => JsonNamingPolicy.CamelCase.ConvertName(group.Key.ToString()), static group => group.Count(), StringComparer.Ordinal),
                Output = sessionOutput,
                Reads = coverage.Count == 0 ? null : [.. coverage.Select(static entry => entry.Line)],
                Warnings = review is null ? null : [review.Warning]
            },
            JsonOptions);

        return await SettleAsync(store, run, nodeRun, nodeRuns, DevWorkflowNodeRunStatus.Succeeded, failureClass: null, terminalReason: null, output, cancellationToken);
    }

    /// <summary>Resumes a session that parked, until the node run's resume budget is spent.</summary>
    /// <remarks>
    ///     Parking is routine rather than a fault: a work session pauses on its own step budget, and a workflow node
    ///     routinely needs more steps than one run allows. Exhausting the budget therefore asks a human rather than
    ///     failing the node — the work so far is on the session, and a person decides whether it needs more.
    /// </remarks>
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
                cancellationToken);
        }

        if (!_sessions.HasCapacity)
        {
            // The slot is held by another session; asked before the repository, so a row waiting its turn does not re-resolve it every tick.
            return 0;
        }

        WorkSessionRuntimeOverride? runtime;
        try
        {
            runtime = RuntimeOf(graph,
                node,
                await RepositoryRootAsync(nodeRun, session.AgentDefinitionId, cancellationToken),
                await ReadableArtifactsAsync(store, run, nodeRun, cancellationToken));
        }
        catch (DevWorkflowValidationException exception)
        {
            // The repository went away mid-run: a resume would read nothing, and a retry answers the same until a person reconnects it.
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.Configuration, exception.Message, cancellationToken);
        }

        if (!await TryDriveAsync(session, runtime, cancellationToken))
        {
            // The slot is held by another session. The row stays Running — it has not stopped working, it waits for its own continuation — and the next tick asks again.
            return 0;
        }

        // Recorded AFTER the resume landed, keyed by the resume index so a replayed tick cannot spend the budget twice. The attach event is also the per-attempt history the single-row
        // node-run schema does not keep.
        _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                WorkSessionId = session.Id,
                OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, $"resume-{nodeRun.SessionResumes}"),
                CountsAsResume = true
            },
            cancellationToken);
        return 1;
    }

    /// <summary>The session the row is still carrying, if it has one and it still exists.</summary>
    private async Task<WorkSessionDetail?> TryReadAttachedAsync(DevWorkflowNodeRunSnapshot nodeRun, CancellationToken cancellationToken) =>
        nodeRun.WorkSessionId is { } attached ? await TryReadAsync(attached, cancellationToken) : null;

    private async Task<WorkSessionDetail?> TryReadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            return await _sessions.GetAsync(sessionId, cancellationToken);
        }
        catch (WorkSessionNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The sentence the write-declaration gate stopped this session with, or <see langword="null" /> when nothing
    ///     stopped it that way.
    /// </summary>
    /// <remarks>
    ///     Read off the step row the supervisor wrote at the moment of the refusal — the outcome tag is the stable
    ///     code and its detail the sentence — so the answer is a fact about what happened rather than a verdict about
    ///     what the agent definition currently allows. The LAST such row wins for the same reason a terminal reason
    ///     does: it is the one the session settled on.
    /// </remarks>
    private async Task<string?> ReadWriteGateRefusalAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var events = await _sessionStore.ListEventsAsync(sessionId, sinceSequence: 0, cancellationToken);
        var refused = events.LastOrDefault(static entry => string.Equals(entry.Outcome, WorkSessionEventTypes.WriteGateOutcome, StringComparison.Ordinal));
        return WorkSessionEventTypes.ReadWriteGateDetail(refused?.DetailJson);
    }

    private static string FailureOutput(DevWorkflowNodeRunSnapshot nodeRun, WorkSessionDetail session, string failureClass) =>
        JsonSerializer.Serialize(new AgentOutput
            {
                Status = DevWorkflowNodeOutputStatuses.Failed,
                Attempt = nodeRun.Attempt,
                FailureClass = failureClass,
                SessionStatus = JsonNamingPolicy.CamelCase.ConvertName(session.Status.ToString()),
                SessionResumes = nodeRun.SessionResumes,
                ArtifactCount = 0,
                Findings = new Dictionary<string, int>(StringComparer.Ordinal)
            },
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
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                TargetStatus = target,
                OutputJson = outputJson,
                FailureClass = failureClass,
                TerminalReason = terminalReason,
                WorkItemStatus = DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, nodeRuns, nodeRun.Id, target)
            },
            cancellationToken);
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
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                TargetStatus = DevWorkflowNodeRunStatus.Blocked,
                PendingDecisionKind = DevWorkflowDecisionKind.Abandon,
                FailureClass = failureClass,
                TerminalReason = sanitizedReason,
                WorkItemStatus = DevWorkflowWorkItemStatus.Blocked
            },
            cancellationToken);
        return 1;
    }

    /// <summary>The agent node's slice of the output document every executor writes.</summary>
    private sealed record AgentOutput
    {
        public required string Status { get; init; }

        public required int Attempt { get; init; }

        public required string? FailureClass { get; init; }

        public required string SessionStatus { get; init; }

        public required int SessionResumes { get; init; }

        public required int ArtifactCount { get; init; }

        public required IReadOnlyDictionary<string, int> Findings { get; init; }

        /// <summary>What the session handed in under its node's output contract, where an <c>output.*</c> edge reads it; absent without one.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Output { get; init; }

        /// <summary>One coverage line per readable text artifact, off the session's <c>ArtifactRead</c> events; absent when it was handed none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? Reads { get; init; }

        /// <summary>What the run should know about this success without it blocking anything; absent when there is nothing.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? Warnings { get; init; }
    }

    /// <summary>The detail of a <c>node.reviewer.same-model</c> event: the shared model, the patches it produced, and the sentence the output carries.</summary>
    private sealed record SameModelReview
    {
        /// <summary>The node-run attempt it warns about, so a reader shows a retried node's current warning rather than an earlier attempt's.</summary>
        public required int Attempt { get; init; }

        public required string ModelId { get; init; }

        public required IReadOnlyList<string> Patches { get; init; }

        public required string Warning { get; init; }
    }

    /// <summary>How much of one readable artifact a session read: the union of the pages it was served, in characters.</summary>
    private sealed class ArtifactCoverage
    {
        public required WorkSessionReadableArtifact Artifact { get; init; }

        public required int Read { get; init; }

        /// <summary>The artifact's length in characters as a served page reported it, or null when no page was served.</summary>
        public required int? Total { get; init; }

        public bool Complete => Read == Total;

        public string Line =>
            Total is { } total
                ? string.Create(CultureInfo.InvariantCulture, $"{Artifact.Name} ({Artifact.Producer}) read {Read} of {total} characters")
                : $"{Artifact.Name} ({Artifact.Producer}) not read";

        public static ArtifactCoverage Of(WorkSessionReadableArtifact artifact, IEnumerable<WorkSessionArtifactReadDetail> pages)
        {
            var read = 0;
            var end = 0;
            int? total = artifact.SizeBytes == 0 ? 0 : null;
            foreach (var page in pages.OrderBy(static page => page.Offset))
            {
                total = page.Total;
                var from = Math.Max(page.Offset, end);
                var to = page.Offset + page.Length;
                if (to > from)
                {
                    read += to - from;
                    end = to;
                }
            }

            return new ArtifactCoverage
            {
                Artifact = artifact,
                Read = read,
                Total = total
            };
        }
    }
}
