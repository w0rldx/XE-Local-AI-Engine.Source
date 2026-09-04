namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;

using XE_Local_AI_Engine.Client.Common.Telemetry;
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

        // How many CHILDREN the decomposition produced, counted over the run's WHOLE node-run list, once: a client
        // counting the rows it drew is wrong by construction for a fan-out wider than its page. Distinct INDEXES, not
        // rows — a template subtree of more than one node clones every one of them per child, so counting rows would
        // read a two-child fan-out of a two-node template as "1 of 4" against a MaterializationIndex that only ever
        // counts children.
        var materializationCounts = detail.NodeRuns.Where(static nodeRun => nodeRun.MaterializedFromNodeRunId is not null)
                                          .GroupBy(static nodeRun => nodeRun.MaterializedFromNodeRunId!.Value)
                                          .ToDictionary(static group => group.Key,
                                              static group => group.Select(static nodeRun => nodeRun.MaterializationIndex).Distinct().Count());
        // The state machine's own verdict on every skipped row, resolved ONCE for the run: a skip a person chose is
        // waived and a join carries on past it, one that cascaded off something dead is not. A client cannot tell them
        // apart — the ancestor that decides it need not be among the join's own dependencies — so the answer ships on
        // the row rather than being re-derived in the browser.
        var waivedSkips = DevWorkflowGraphContract.WaivedSkipNodeKeys(run.GraphJson, byKey);

        // The cap each node's DEFINITION declared, resolved once for the run off the same pinned graph everything else
        // here reads.
        var declaredCaps = DevWorkflowGraphContract.DeclaredMaxAttempts(run.GraphJson);
        var nodes = detail.NodeRuns
                          .Select(nodeRun => ToSummary(nodeRun,
                              nodesByKey.GetValueOrDefault(nodeRun.NodeKey),
                              graph,
                              byKey,
                              templates,
                              keysByNodeRunId,
                              materializationCounts,
                              agentsById,
                              staleInputs.Contains(nodeRun.Id),
                              SkipWaived(nodeRun, waivedSkips),
                              OperatorRetries(nodeRun, declaredCaps)))
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
            run.LastSequence,

            // Summed over the node runs already loaded above: the rollup costs no extra query, and a run's own row
            // carries no cost of its own to disagree with.
            Cost: RunCost(detail.NodeRuns));
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
            OperatorRetries(nodeRun, DevWorkflowGraphContract.DeclaredMaxAttempts(run.GraphJson)),
            nodeRun.StartedAtUtc,
            nodeRun.EndedAtUtc,
            nodeRun.Sequence,

            // Named from here on: the tail is a run of same-typed optional slots, so a positional call would compile
            // silently misaligned if a field is spliced in ahead of them.
            InputTokens: nodeRun.InputTokens,
            OutputTokens: nodeRun.OutputTokens,
            ReasoningTokens: nodeRun.ReasoningTokens,
            EstimatedInputTokens: nodeRun.EstimatedInputTokens,
            ProviderCalls: nodeRun.ProviderCalls,
            ToolCalls: nodeRun.ToolCalls,
            ToolSchemaTokens: nodeRun.ToolSchemaTokens,
            ToolNames: DevWorkflowNodeRunDocuments.ToolNames(nodeRun.ToolNamesJson),
            AgentTurnMs: nodeRun.AgentTurnMs,
            ServedModelName: nodeRun.ServedModelName,
            Route: Route(nodeRun.RouteJson),
            WorkSessionSteps: nodeRun.WorkSessionSteps,
            FailureClassGroup: AgentUnitFailureClass.FromDevWorkflowFailureClass(nodeRun.FailureClass));
    }

    private static DevWorkflowNodeRunSummaryResponse ToSummary(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowGraphNode? node,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey,
        IReadOnlySet<string> templates,
        IReadOnlyDictionary<Guid, string> keysByNodeRunId,
        IReadOnlyDictionary<Guid, int> materializationCounts,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agentsById,
        bool hasStaleInputs,
        bool? skipWaived,
        int operatorRetries) =>
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

            // Named from here on: the record carries five consecutive Guid? slots, so a positional call would compile
            // silently misaligned if a future field is spliced in ahead of them.
            MaterializationGroupId: nodeRun.MaterializedFromNodeRunId,
            MaterializationCount: nodeRun.MaterializedFromNodeRunId is { } group && materializationCounts.TryGetValue(group, out var count) ? count : null,
            DevelopmentProjectId: nodeRun.DevelopmentProjectId,
            DevelopmentTaskId: nodeRun.DevelopmentTaskId,
            AgentDefinitionId: nodeRun.AgentDefinitionId,
            AgentDisplayName: AgentDisplayName(nodeRun, node, agentsById),
            ModelLabel: ModelLabel(nodeRun, node, agentsById),
            HasStaleInputs: hasStaleInputs,
            StartedAtUtc: nodeRun.StartedAtUtc,
            CompletedAtUtc: nodeRun.EndedAtUtc,
            Sequence: nodeRun.Sequence,
            OperatorRetries: operatorRetries,
            SkipWaived: skipWaived,
            InputTokens: nodeRun.InputTokens,
            OutputTokens: nodeRun.OutputTokens,
            ToolCalls: nodeRun.ToolCalls,

            // Asked of the contract, not of a spelling of the token repeated here: the same verdict decides the
            // drill-down's note, this row's badge and whether the run header counts the row as work.
            ValidationNotApplicable: DevWorkflowGraphContract.ValidationWasNotApplicable(nodeRun.OutputJson));

    /// <summary>
    ///     How many attempts an operator has bought this node run: the distance the row's own <c>MaxAttempts</c> has
    ///     travelled from the cap its definition declared, which each Retry raises by one IN PLACE. The widening is
    ///     therefore its own record, and the client subtracts this to recover the declared cap.
    ///     <para>
    ///         Read off the row rather than counted from <c>Retry</c> decision rows, because a decision row exists
    ///         whether or not it was ever applied and whether or not widening existed when it was written. Counting
    ///         them reported a widening in two cases that never had one: the settle window between recording a Retry
    ///         and the dispatcher spending it, which a PAUSED run holds open indefinitely; and every node run
    ///         persisted BEFORE this shipped, whose Retry rows moved no cap at all — both rendered a cap one below
    ///         the definition's.
    ///     </para>
    ///     <para>
    ///         A MATERIALIZED clone is looked up by its own key and needs no template hop: the materializer DEEP-CLONES
    ///         the template node into the graph's node array, rewriting only <c>nodeKey</c> and <c>retryTarget</c>, and
    ///         the expansion is written back to <c>run.GraphJson</c> under a bumped revision. So the pinned graph a run
    ///         detail reads declares every clone key, carrying the template's own <c>maxAttempts</c>.
    ///     </para>
    ///     <para>
    ///         Floored at zero, and zero for a node key the pinned graph does not declare — an unroutable graph, which
    ///         <see cref="DevWorkflowGraphContract.DeclaredMaxAttempts" /> answers empty for. Nothing to compare
    ///         against is not evidence of a widening.
    ///     </para>
    /// </summary>
    private static int OperatorRetries(DevWorkflowNodeRunSnapshot nodeRun, IReadOnlyDictionary<string, int> declaredCaps) =>
        declaredCaps.TryGetValue(nodeRun.NodeKey, out var declared) ? Math.Max(0, nodeRun.MaxAttempts - declared) : 0;

    /// <summary>
    ///     The waived verdict as the wire carries it: only a <c>Skipped</c> row has one, and a run whose pinned graph
    ///     could not be parsed has none at all — both read as <c>null</c>, which the client renders as a skip it makes
    ///     no claim about rather than as a dead one.
    /// </summary>
    private static bool? SkipWaived(DevWorkflowNodeRunSnapshot nodeRun, IReadOnlySet<string>? waivedSkips) =>
        waivedSkips is null || nodeRun.Status != DevWorkflowNodeRunStatus.Skipped ? null : waivedSkips.Contains(nodeRun.NodeKey);

    /// <summary>
    ///     The run's spend, added member by member over its node runs. A member stays null until some row reports it,
    ///     so a run whose nodes never measured anything says "nobody measured" rather than "zero".
    /// </summary>
    private static DevWorkflowRunCostResponse RunCost(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        long? inputTokens = null;
        long? outputTokens = null;
        int? toolCalls = null;
        int? providerCalls = null;
        long? agentTurnMs = null;
        foreach (var nodeRun in nodeRuns)
        {
            inputTokens = Add(inputTokens, nodeRun.InputTokens);
            outputTokens = Add(outputTokens, nodeRun.OutputTokens);
            toolCalls = Add(toolCalls, nodeRun.ToolCalls);
            providerCalls = Add(providerCalls, nodeRun.ProviderCalls);
            agentTurnMs = Add(agentTurnMs, nodeRun.AgentTurnMs);
        }

        return new DevWorkflowRunCostResponse(inputTokens, outputTokens, toolCalls, providerCalls, agentTurnMs);
    }

    private static long? Add(long? total, long? term) => term is { } value ? (total ?? 0) + value : total;

    private static int? Add(int? total, int? term) => term is { } value ? (total ?? 0) + value : total;

    /// <summary>
    ///     The stored route on the wire, parsed by the runtime's own reader — the one that owns the document — and only
    ///     re-shaped here. An unreadable column costs this node its route rather than costing the drill-down a 500,
    ///     exactly as an unreadable policy resolution does.
    /// </summary>
    private static DevWorkflowNodeRouteResponse? Route(string? routeJson) =>
        DevWorkflowNodeRunDocuments.TryParseRoute(routeJson) is { } route
            ? new DevWorkflowNodeRouteResponse(route.Satisfied, route.Dead, route.Waived, route.GateAnswer, route.Truncated)
            : null;

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

    /// <summary>
    ///     The model this node run's session actually runs on: the node's own <c>modelProfile</c> when it authored one,
    ///     because that is the pin the work session is created and resumed with, and the bound agent's otherwise. Null
    ///     when neither pins anything — the session then takes the node's default chat model, which is a live setting
    ///     this pane has no business naming as if the run had chosen it.
    ///     <para>
    ///         The authored pin counts only on an AGENT node run, which is the one lane that dispatches on it. A Tool or
    ///         DevTask node carrying the field runs on neither — Dev Mode's coder resolves its own — so naming it there
    ///         would be the same false label this method was cut back to stop giving.
    ///     </para>
    ///     <para>
    ///         Only the PINNED half is stable: the node comes from the run's own graph snapshot, so an edit to the
    ///         definition cannot change it. The fallback is the agent definition as it stands NOW, so a node that
    ///         pinned nothing re-labels when its agent is repointed — which is the same live read every other agent
    ///         field on this response makes.
    ///     </para>
    ///     <para>
    ///         Trimmed, because the parser trims before it pins: labelling <c>" qwen "</c> for a session dispatched on
    ///         <c>"qwen"</c> would make the pane disagree with the run over whitespace.
    ///     </para>
    /// </summary>
    private static string? ModelLabel(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowGraphNode? node,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agentsById) =>
        PinnedModel(nodeRun, node)
        ?? (nodeRun.AgentDefinitionId is { } id && agentsById.TryGetValue(id, out var agent) ? agent.ModelProfile : null);

    /// <summary>The node's own pin as the RUNTIME will read it — trimmed, blank treated as absent — and only where a node run dispatches on one.</summary>
    private static string? PinnedModel(DevWorkflowNodeRunSnapshot nodeRun, DevWorkflowGraphNode? node) =>
        nodeRun.NodeType == DevWorkflowNodeType.Agent && node?.ModelProfile?.Trim() is { Length: > 0 } pinned ? pinned : null;

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
