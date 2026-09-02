namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Composes the two read shapes that need more than one row: a run with its pinned graph and every node summary,
///     and one node run's drill-down.
///     <para>
///         Run detail is THE repaint fetch, so it is written to a fixed query budget rather than a per-node one: the
///         run and its node runs, the definition names, the agent definitions, and the artifact rows. A per-node query
///         here would be an N+1 on the one request a live view repeats.
///     </para>
/// </summary>
public sealed class DevWorkflowRunComposer(IDevWorkflowStore store, IAgentDefinitionStore agents, IAgentWorkSessionStore sessions)
{
    private readonly IAgentDefinitionStore _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly IAgentWorkSessionStore _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<DevWorkflowRunResponse> ComposeAsync(DevWorkflowRunDetail detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var run = detail.Run;
        var graph = DevWorkflowContractMapper.ToWireGraph(run.GraphJson);
        var nodesByKey = graph.Nodes.ToDictionary(static node => node.NodeKey, StringComparer.Ordinal);
        var keysByNodeRunId = detail.NodeRuns.ToDictionary(static nodeRun => nodeRun.Id, static nodeRun => nodeRun.NodeKey);
        var byKey = detail.NodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        var agentsById = await ResolveAgentsAsync(detail.NodeRuns, cancellationToken).ConfigureAwait(false);
        var staleInputs = await ResolveStaleInputsAsync(run.Id, detail.NodeRuns, cancellationToken).ConfigureAwait(false);

        var definitions = await _store.ListDefinitionsAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
        var definitionName = definitions.FirstOrDefault(definition => definition.Id == run.DefinitionId)?.Name;

        // Read off the wire graph, which already carries the parser's answer per node: one parse for the run, not one
        // per node run, and not a second walk that could disagree with the one the dispatcher admits by.
        var templates = graph.Nodes.Where(static node => node.IsTemplate == true)
                             .Select(static node => node.NodeKey)
                             .ToHashSet(StringComparer.Ordinal);

        // Counted over the run's WHOLE node-run list, once: a client counting the rows it drew is wrong by
        // construction for a fan-out wider than its page.
        var materializationCounts = detail.NodeRuns.Where(static nodeRun => nodeRun.MaterializedFromNodeRunId is not null)
                                          .GroupBy(static nodeRun => nodeRun.MaterializedFromNodeRunId!.Value)
                                          .ToDictionary(static group => group.Key, static group => group.Count());
        var nodes = detail.NodeRuns
                          .Select(nodeRun => ToSummary(nodeRun,
                              nodesByKey.GetValueOrDefault(nodeRun.NodeKey),
                              graph,
                              byKey,
                              templates,
                              keysByNodeRunId,
                              materializationCounts,
                              agentsById,
                              staleInputs.Contains(nodeRun.Id)))
                          .ToList();

        return new DevWorkflowRunResponse(run.Id,
            run.WorkItemId,
            run.DefinitionId,
            run.DefinitionVersion,
            definitionName,
            run.GraphRevision,
            graph,
            run.Status.ToString(),
            nodes,
            detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Queued),
            detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Running),
            detail.PendingDecisionCount,
            detail.BlockingGateNodeRunId,
            run.FailureClass,
            run.TerminalReason,
            run.StartedAtUtc,
            run.EndedAtUtc,
            run.Version,
            run.LastSequence);
    }

    public async Task<DevWorkflowNodeRunDetailResponse> ComposeNodeAsync(Guid runId, Guid nodeRunId, CancellationToken cancellationToken)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var nodeRun = await _store.GetNodeRunAsync(nodeRunId, cancellationToken).ConfigureAwait(false);
        if (nodeRun.RunId != runId)
        {
            // Reads as absent rather than as another run's node, so one run's route can never surface another's rows.
            throw new DevWorkflowNotFoundException($"Development workflow node run '{nodeRunId}' was not found on run '{runId}'.");
        }

        var graph = DevWorkflowContractMapper.ToWireGraph(run.GraphJson);
        var node = graph.Nodes.FirstOrDefault(entry => string.Equals(entry.NodeKey, nodeRun.NodeKey, StringComparison.Ordinal));
        var agentsById = await ResolveAgentsAsync([nodeRun], cancellationToken).ConfigureAwait(false);

        var artifacts = await _store.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        var produced = artifacts.Where(artifact => artifact.ProducedByNodeRunId == nodeRunId).OrderBy(static artifact => artifact.Sequence).ToList();
        var consumed = await _store.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken).ConfigureAwait(false);
        var decisions = await _store.ListDecisionsAsync(runId, cancellationToken).ConfigureAwait(false);

        // One list, and only when this node actually recorded a resolution: rule sets are a handful of bodyless rows,
        // so listing them beats a lookup per recorded id, and a node with no policy pays nothing at all.
        var ruleSets = nodeRun.PolicyResolutionJson is null
            ? []
            : await _store.ListRuleSetsAsync(cancellationToken).ConfigureAwait(false);

        // Read from the other family on the loose session id, never stored here: a purged session leaves the node run
        // intact and the drill-down renders "transcript no longer available" instead of a broken link.
        Guid? conversationId = null;
        if (nodeRun is { WorkSessionId: { } sessionId, WorkSessionAvailable: true })
        {
            conversationId = (await _sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)).ConversationId;
        }

        return new DevWorkflowNodeRunDetailResponse(nodeRun.Id,
            nodeRun.RunId,
            nodeRun.NodeKey,
            nodeRun.NodeType.ToString(),
            node?.Label ?? nodeRun.NodeKey,
            nodeRun.Status.ToString(),
            nodeRun.Attempt,
            nodeRun.MaxAttempts,
            nodeRun.SessionResumes,
            nodeRun.QueueReason,
            nodeRun.QueuedAtUtc,
            nodeRun.AgentDefinitionId,
            AgentDisplayName(nodeRun, node, agentsById),
            ModelLabel(nodeRun, node, agentsById),
            nodeRun.WorkSessionId,
            conversationId,
            nodeRun.WorkSessionAvailable,
            nodeRun.DevelopmentProjectId,
            nodeRun.DevelopmentTaskId,

            // The node's headline output: the newest version it produced, which is the one a review panel opens.
            produced.LastOrDefault(static artifact => artifact.IsLatest)?.Id ?? produced.LastOrDefault()?.Id,
            node?.Instructions,
            nodeRun.InputJson,
            nodeRun.OutputJson,
            [.. produced.Select(static artifact => artifact.Id)],
            consumed,
            AppliedRuleSets(nodeRun.PolicyResolutionJson, ruleSets),
            nodeRun.PendingDecisionKind?.ToString(),
            DevWorkflowGraphContract.AllowedDecisions(nodeRun.Status),

            // Only a human gate produces the answer an out-edge condition reads, so only there does the question mean
            // anything. False here is what tells the confirm dialog that a rejection ENDS the run.
            nodeRun.NodeType == DevWorkflowNodeType.HumanGate && DevWorkflowGraphContract.HasRejectBranch(run.GraphJson, nodeRun.NodeKey),
            nodeRun.FailureClass,
            nodeRun.TerminalReason,
            [.. decisions.Where(decision => decision.NodeRunId == nodeRunId).OrderBy(static decision => decision.Sequence).Select(DevWorkflowContractMapper.ToResponse)],
            nodeRun.StartedAtUtc,
            nodeRun.EndedAtUtc,
            nodeRun.Sequence);
    }

    private static DevWorkflowNodeRunSummaryResponse ToSummary(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowGraphNode? node,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey,
        IReadOnlySet<string> templates,
        IReadOnlyDictionary<Guid, string> keysByNodeRunId,
        IReadOnlyDictionary<Guid, int> materializationCounts,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agentsById,
        bool hasStaleInputs) =>
        new(nodeRun.Id,
            nodeRun.NodeKey,
            nodeRun.NodeType.ToString(),
            node?.Label ?? nodeRun.NodeKey,
            nodeRun.Status.ToString(),
            nodeRun.Attempt,
            nodeRun.MaxAttempts,
            nodeRun.QueueReason,
            nodeRun.QueuedAtUtc,
            WaitingOnNodeKeys(nodeRun, graph, byKey, templates),
            nodeRun.PendingDecisionKind?.ToString(),
            nodeRun.MaterializedFromNodeRunId is not null,
            nodeRun.MaterializedFromNodeRunId is { } parent ? keysByNodeRunId.GetValueOrDefault(parent) : null,
            nodeRun.MaterializationIndex,
            nodeRun.MaterializedFromNodeRunId,
            nodeRun.MaterializedFromNodeRunId is { } group && materializationCounts.TryGetValue(group, out var count) ? count : null,
            nodeRun.DevelopmentProjectId,
            nodeRun.DevelopmentTaskId,
            nodeRun.AgentDefinitionId,
            AgentDisplayName(nodeRun, node, agentsById),
            ModelLabel(nodeRun, node, agentsById),
            hasStaleInputs,
            nodeRun.StartedAtUtc,
            nodeRun.EndedAtUtc,
            nodeRun.Sequence);

    /// <summary>
    ///     Which upstream nodes a <c>Pending</c> node run is still waiting on, computed here rather than left to the
    ///     client: re-deriving join semantics in the browser would duplicate the dispatcher's own evaluation and drift
    ///     from it. Only <c>Pending</c> carries it — <c>Blocked</c> means a human is the dependency.
    ///     <para>
    ///         A source that has SETTLED is never waited on, whichever way it settled — which is the same answer the
    ///         dispatcher's edge rule gives for a branch whose condition did not fire, and is why an <c>Any</c> join
    ///         needs no case of its own here: it too waits exactly while an inbound edge is undecided. The one edge that
    ///         needs saying out loud is a materialization TEMPLATE's: it never gets a row, so it can never settle, and
    ///         naming it would show every decomposing run as stuck on the one node nothing ever runs.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string>? WaitingOnNodeKeys(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey,
        IReadOnlySet<string> templates)
    {
        if (nodeRun.Status != DevWorkflowNodeRunStatus.Pending)
        {
            return null;
        }

        var waiting = graph.Edges.Where(edge => string.Equals(edge.To, nodeRun.NodeKey, StringComparison.Ordinal))
                           .Select(static edge => edge.From)
                           .Where(from => !templates.Contains(from)
                                          && (byKey.GetValueOrDefault(from) is not { } source
                                              || source.Status is not (DevWorkflowNodeRunStatus.Succeeded
                                                  or DevWorkflowNodeRunStatus.Failed
                                                  or DevWorkflowNodeRunStatus.Skipped
                                                  or DevWorkflowNodeRunStatus.Cancelled)))
                           .Distinct(StringComparer.Ordinal)
                           .OrderBy(static key => key, StringComparer.Ordinal)
                           .ToList();
        return waiting.Count == 0 ? null : waiting;
    }

    /// <summary>
    ///     The bound agent's name, or the seed slug the node names when the binding is by slug — which is the Slice-A
    ///     shape, where the node run carries no agent id at all.
    /// </summary>
    private static string? AgentDisplayName(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowGraphNode? node,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agentsById) =>
        nodeRun.AgentDefinitionId is { } id && agentsById.TryGetValue(id, out var agent) ? agent.Name : node?.AgentSeedSlug;

    /// <summary>The node's own override wins: it is what the run will actually use.</summary>
    private static string? ModelLabel(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowGraphNode? node,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agentsById) =>
        node?.ModelProfile
        ?? (nodeRun.AgentDefinitionId is { } id && agentsById.TryGetValue(id, out var agent) ? agent.ModelProfile : null);

    /// <summary>
    ///     One read for every id-bound node run, and none at all when there are none — which is every graph that binds
    ///     its agents by slug.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, AgentDefinitionRecord>> ResolveAgentsAsync(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        if (!nodeRuns.Any(static nodeRun => nodeRun.AgentDefinitionId is not null))
        {
            return new Dictionary<Guid, AgentDefinitionRecord>();
        }

        // ponytail: lists every agent definition to name a handful. Definitions are few and the alternative is one
        // read per node; a name-only projection on the agent store is the upgrade if a repaint ever feels it.
        var definitions = await _agents.ListAsync(cancellationToken).ConfigureAwait(false);
        return definitions.ToDictionary(static definition => definition.Id);
    }

    /// <summary>
    ///     Which node runs consumed an artifact that has since been superseded. Staleness IS written today, by the two
    ///     callers that supersede an artifact — an agent-node promotion and a Tool node's report, both through
    ///     <c>MarkDependentsStaleAsync</c> — so this answers a real question rather than a reserved one. It is still
    ///     "none" for most runs, and costs one artifact read to say so: the per-node read only happens once a stale row
    ///     actually exists.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ResolveStaleInputsAsync(Guid runId,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        var artifacts = await _store.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        var stale = artifacts.Where(static artifact => artifact.IsStale).Select(static artifact => artifact.Id).ToHashSet();
        if (stale.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // ponytail: one read per node run, and only while a stale artifact exists on the run. A grouped
        // "uses joined to stale artifacts" store query is the upgrade when staleness starts being written.
        var affected = new HashSet<Guid>();
        foreach (var nodeRunId in nodeRuns.Select(static nodeRun => nodeRun.Id))
        {
            var consumed = await _store.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken).ConfigureAwait(false);
            if (consumed.Any(stale.Contains))
            {
                _ = affected.Add(nodeRunId);
            }
        }

        return affected;
    }

    /// <summary>
    ///     Which rule text applied, read from the record written at materialization — never re-resolved. Re-resolving
    ///     would answer "what would apply now", a different and misleading question in an audit view.
    ///     <para>
    ///         The CURRENT hash of each named rule set rides alongside it, so a reader can tell an unchanged document
    ///         from one edited since the node ran, and both from one deleted — which reads as a null current hash.
    ///     </para>
    ///     <para>
    ///         Parsed through the runtime's own tolerant reader: an unreadable column is a hand-edited row, and it must
    ///         cost this node its rule-set list rather than costing the whole drill-down a 500.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<DevWorkflowAppliedRuleSetResponse> AppliedRuleSets(string? policyResolutionJson, IReadOnlyList<DevWorkflowRuleSetSummary> current) =>
    [
        .. DevWorkflowRulePolicyResolver.Read(policyResolutionJson)
                                        .Select(applied => new DevWorkflowAppliedRuleSetResponse(applied.Id,
                                            applied.Name,
                                            applied.ContentSha256,
                                            current.FirstOrDefault(ruleSet => ruleSet.Id == applied.Id)?.ContentSha256))
    ];
}
