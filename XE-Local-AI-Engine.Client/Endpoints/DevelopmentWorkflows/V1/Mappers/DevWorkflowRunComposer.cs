namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Composes the two read shapes that need more than one row: a run with its pinned graph and every node summary,
///     and one node run's drill-down.
/// </summary>
/// <remarks>
///     Run detail is THE repaint fetch, so it runs a fixed query budget rather than a per-node one, which would be an
///     N+1 on the one request a live view repeats. Built by DI and reached only from the endpoints in this folder, so
///     it lives under the same fence they do: it composes <c>Client.Application</c> services and takes no store of its
///     own. Every derived field is explained in docs/wiki/09-api-and-hubs.md
///     ("Design notes on the newer endpoint families").
/// </remarks>
public sealed class DevWorkflowRunComposer
{
    private readonly IAgentDefinitionService _agents;
    private readonly DevWorkflowAuthoringService _authoring;
    private readonly DevWorkflowRunQueryService _queries;
    private readonly IWorkSessionService _sessions;

    public DevWorkflowRunComposer(DevWorkflowRunQueryService queries,
        DevWorkflowAuthoringService authoring,
        IAgentDefinitionService agents,
        IWorkSessionService sessions)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(sessions);
        _agents = agents;
        _authoring = authoring;
        _queries = queries;
        _sessions = sessions;
    }

    public async Task<DevWorkflowRunResponse> ComposeAsync(DevWorkflowRunDetail detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var run = detail.Run;
        var graph = DevWorkflowContractMapper.ToWireGraph(run.GraphJson);
        var nodesByKey = graph.Nodes.ToDictionary(static node => node.NodeKey, StringComparer.Ordinal);
        var keysByNodeRunId = detail.NodeRuns.ToDictionary(static nodeRun => nodeRun.Id, static nodeRun => nodeRun.NodeKey);
        var byKey = detail.NodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        var agentsById = await ResolveAgentsAsync(detail.NodeRuns, cancellationToken);
        var staleInputs = await ResolveStaleInputsAsync(run.Id, detail.NodeRuns, cancellationToken);

        var definitions = await _authoring.ListDefinitionsAsync(includeArchived: true, cancellationToken);
        var definitionName = definitions.FirstOrDefault(definition => definition.Id == run.DefinitionId)?.Name;

        // Read off the wire graph, which already carries the parser's answer per node: one parse for the run, not one
        // per node run, and not a second walk that could disagree with the one the dispatcher admits by.
        var templates = graph.Nodes.Where(static node => node.IsTemplate == true)
                             .Select(static node => node.NodeKey)
                             .ToHashSet(StringComparer.Ordinal);

        // How many CHILDREN the decomposition produced, over the run's WHOLE node-run list and over distinct INDEXES rather than rows: a client counting the rows it drew is
        // wrong for a fan-out wider than its page, and a multi-node template subtree clones every node per child, so rows read a two-child fan-out of a two-node template as "1 of 4".
        var materializationCounts = detail.NodeRuns.Where(static nodeRun => nodeRun.MaterializedFromNodeRunId is not null)
                                          .GroupBy(static nodeRun => nodeRun.MaterializedFromNodeRunId!.Value)
                                          .ToDictionary(static group => group.Key,
                                              static group => group.Select(static nodeRun => nodeRun.MaterializationIndex).Distinct().Count());
        // The state machine's own verdict on every skipped row, resolved ONCE for the run: a skip a person chose is waived and a join carries on past it, one that cascaded
        // off something dead is not. A client cannot tell them apart — the deciding ancestor need not be among the join's dependencies — so the answer ships on the row.
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

        return new DevWorkflowRunResponse
        {
            Id = run.Id,
            WorkItemId = run.WorkItemId,
            DefinitionId = run.DefinitionId,
            DefinitionVersion = run.DefinitionVersion,
            DefinitionName = definitionName,
            GraphRevision = run.GraphRevision,
            Graph = graph,
            Status = run.Status.ToString(),
            Nodes = nodes,
            QueuedNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Queued),
            RunningNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Running),
            PendingDecisionCount = detail.PendingDecisionCount,
            BlockingGateNodeRunId = detail.BlockingGateNodeRunId,
            FailureClass = run.FailureClass,
            TerminalReason = run.TerminalReason,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.EndedAtUtc,
            Version = run.Version,
            LastSequence = run.LastSequence,
            // Summed over the node runs already loaded above: the rollup costs no extra query, and a run's own row
            // carries no cost of its own to disagree with.
            Cost = RunCost(detail.NodeRuns)
        };
    }

    public async Task<DevWorkflowNodeRunDetailResponse> ComposeNodeAsync(Guid runId, Guid nodeRunId, CancellationToken cancellationToken)
    {
        var run = await _queries.GetRunAsync(runId, cancellationToken);
        var nodeRun = await _queries.GetNodeRunAsync(nodeRunId, cancellationToken);
        if (nodeRun.RunId != runId)
        {
            // Reads as absent rather than as another run's node, so one run's route can never surface another's rows.
            throw new DevWorkflowNotFoundException($"Development workflow node run '{nodeRunId}' was not found on run '{runId}'.");
        }

        var graph = DevWorkflowContractMapper.ToWireGraph(run.GraphJson);
        var node = graph.Nodes.FirstOrDefault(entry => string.Equals(entry.NodeKey, nodeRun.NodeKey, StringComparison.Ordinal));
        var agentsById = await ResolveAgentsAsync([nodeRun], cancellationToken);

        var artifacts = await _queries.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken);
        var produced = artifacts.Where(artifact => artifact.ProducedByNodeRunId == nodeRunId).OrderBy(static artifact => artifact.Sequence).ToList();
        var consumed = await _queries.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken);
        var decisions = await _queries.ListDecisionsAsync(runId, cancellationToken);

        // One list, and only when this node actually recorded a resolution: rule sets are a handful of bodyless rows,
        // so listing them beats a lookup per recorded id, and a node with no policy pays nothing at all.
        var ruleSets = nodeRun.PolicyResolutionJson is null
            ? []
            : await _authoring.ListRuleSetsAsync(cancellationToken);

        // Read from the other family on the loose session id, never stored here: a purged session leaves the node run
        // intact and the drill-down renders "transcript no longer available" instead of a broken link.
        Guid? conversationId = null;
        if (nodeRun is { WorkSessionId: { } sessionId, WorkSessionAvailable: true })
        {
            conversationId = (await _sessions.GetAsync(sessionId, cancellationToken)).ConversationId;
        }

        return new DevWorkflowNodeRunDetailResponse
        {
            Id = nodeRun.Id,
            RunId = nodeRun.RunId,
            NodeKey = nodeRun.NodeKey,
            NodeType = nodeRun.NodeType.ToString(),
            Label = node?.Label ?? nodeRun.NodeKey,
            Status = nodeRun.Status.ToString(),
            Attempt = nodeRun.Attempt,
            MaxAttempts = nodeRun.MaxAttempts,
            SessionResumes = nodeRun.SessionResumes,
            QueueReason = nodeRun.QueueReason,
            QueuedAtUtc = nodeRun.QueuedAtUtc,
            AgentDefinitionId = nodeRun.AgentDefinitionId,
            AgentDisplayName = AgentDisplayName(nodeRun, node, agentsById),
            ModelLabel = ModelLabel(nodeRun, node, agentsById),
            WorkSessionId = nodeRun.WorkSessionId,
            ConversationId = conversationId,
            WorkSessionAvailable = nodeRun.WorkSessionAvailable,
            DevelopmentProjectId = nodeRun.DevelopmentProjectId,
            DevelopmentTaskId = nodeRun.DevelopmentTaskId,
            // The node's headline output: the newest version it produced, which is the one a review panel opens.
            PrimaryArtifactId = produced.LastOrDefault(static artifact => artifact.IsLatest)?.Id ?? produced.LastOrDefault()?.Id,
            Instructions = node?.Instructions,
            InputJson = nodeRun.InputJson,
            OutputJson = nodeRun.OutputJson,
            ProducedArtifactIds = [.. produced.Select(static artifact => artifact.Id)],
            ConsumedArtifactIds = consumed,
            AppliedRuleSets = AppliedRuleSets(nodeRun.PolicyResolutionJson, ruleSets),
            PendingDecisionKind = nodeRun.PendingDecisionKind?.ToString(),
            AllowedDecisions = DevWorkflowGraphContract.AllowedDecisions(nodeRun.Status),
            // Only a human gate produces the answer an out-edge condition reads, so only there does the question mean
            // anything. False here is what tells the confirm dialog that a rejection ENDS the run.
            HasRejectBranch = nodeRun.NodeType == DevWorkflowNodeType.HumanGate && DevWorkflowGraphContract.HasRejectBranch(run.GraphJson, nodeRun.NodeKey),
            FailureClass = nodeRun.FailureClass,
            TerminalReason = nodeRun.TerminalReason,
            Decisions = [.. decisions.Where(decision => decision.NodeRunId == nodeRunId).OrderBy(static decision => decision.Sequence).Select(DevWorkflowContractMapper.ToResponse)],
            OperatorRetries = OperatorRetries(nodeRun, DevWorkflowGraphContract.DeclaredMaxAttempts(run.GraphJson)),
            StartedAtUtc = nodeRun.StartedAtUtc,
            CompletedAtUtc = nodeRun.EndedAtUtc,
            Sequence = nodeRun.Sequence,
            // Named from here on: the tail is a run of same-typed optional slots, so a positional call would compile
            // silently misaligned if a field is spliced in ahead of them.
            InputTokens = nodeRun.InputTokens,
            OutputTokens = nodeRun.OutputTokens,
            ReasoningTokens = nodeRun.ReasoningTokens,
            EstimatedInputTokens = nodeRun.EstimatedInputTokens,
            ProviderCalls = nodeRun.ProviderCalls,
            ToolCalls = nodeRun.ToolCalls,
            ToolSchemaTokens = nodeRun.ToolSchemaTokens,
            ToolNames = DevWorkflowNodeRunDocuments.ToolNames(nodeRun.ToolNamesJson),
            AgentTurnMs = nodeRun.AgentTurnMs,
            ServedModelName = nodeRun.ServedModelName,
            Route = Route(nodeRun.RouteJson),
            WorkSessionSteps = nodeRun.WorkSessionSteps,
            FailureClassGroup = AgentUnitFailureClass.FromDevWorkflowFailureClass(nodeRun.FailureClass),
            ModelReadinessMs = nodeRun.ModelReadinessMs,
            VramFreeAtLoadBytes = nodeRun.VramFreeAtLoadBytes,
            VramAdmittedBytes = nodeRun.VramAdmittedBytes
        };
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
        new()
        {
            Id = nodeRun.Id,
            NodeKey = nodeRun.NodeKey,
            NodeType = nodeRun.NodeType.ToString(),
            Label = node?.Label ?? nodeRun.NodeKey,
            Status = nodeRun.Status.ToString(),
            Attempt = nodeRun.Attempt,
            MaxAttempts = nodeRun.MaxAttempts,
            QueueReason = nodeRun.QueueReason,
            QueuedAtUtc = nodeRun.QueuedAtUtc,
            WaitingOnNodeKeys = WaitingOnNodeKeys(nodeRun, graph, byKey, templates),
            PendingDecisionKind = nodeRun.PendingDecisionKind?.ToString(),
            IsMaterialized = nodeRun.MaterializedFromNodeRunId is not null,
            MaterializedFromNodeKey = nodeRun.MaterializedFromNodeRunId is { } parent ? keysByNodeRunId.GetValueOrDefault(parent) : null,
            MaterializationIndex = nodeRun.MaterializationIndex,
            MaterializationGroupId = nodeRun.MaterializedFromNodeRunId,
            MaterializationCount = nodeRun.MaterializedFromNodeRunId is { } group && materializationCounts.TryGetValue(group, out var count) ? count : null,
            DevelopmentProjectId = nodeRun.DevelopmentProjectId,
            DevelopmentTaskId = nodeRun.DevelopmentTaskId,
            AgentDefinitionId = nodeRun.AgentDefinitionId,
            AgentDisplayName = AgentDisplayName(nodeRun, node, agentsById),
            ModelLabel = ModelLabel(nodeRun, node, agentsById),
            HasStaleInputs = hasStaleInputs,
            StartedAtUtc = nodeRun.StartedAtUtc,
            CompletedAtUtc = nodeRun.EndedAtUtc,
            Sequence = nodeRun.Sequence,
            OperatorRetries = operatorRetries,
            SkipWaived = skipWaived,
            InputTokens = nodeRun.InputTokens,
            OutputTokens = nodeRun.OutputTokens,
            ToolCalls = nodeRun.ToolCalls,
            // Asked of the contract, not of a spelling of the token repeated here: the same verdict decides the
            // drill-down's note, this row's badge and whether the run header counts the row as work.
            ValidationNotApplicable = DevWorkflowGraphContract.ValidationWasNotApplicable(nodeRun.OutputJson)
        };

    /// <summary>
    ///     How many attempts an operator has bought this node run: the distance the row's own <c>MaxAttempts</c> has
    ///     travelled from the cap its definition declared, which each Retry raises by one IN PLACE.
    /// </summary>
    /// <remarks>
    ///     Read off the row, never counted from <c>Retry</c> decision rows, which exist whether or not they were ever
    ///     applied. Floored at zero, and zero for a node key the pinned graph does not declare — an unroutable graph,
    ///     which <see cref="DevWorkflowGraphContract.DeclaredMaxAttempts" /> answers empty for: nothing to compare
    ///     against is not evidence of a widening. Why the row and not the decisions, and why a materialized clone needs
    ///     no template hop: docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
    /// </remarks>
    private static int OperatorRetries(DevWorkflowNodeRunSnapshot nodeRun, IReadOnlyDictionary<string, int> declaredCaps) =>
        declaredCaps.TryGetValue(nodeRun.NodeKey, out var declared) ? Math.Max(0, nodeRun.MaxAttempts - declared) : 0;

    /// <summary>
    ///     The waived verdict as the wire carries it: only a <c>Skipped</c> row has one, and a run whose pinned graph
    ///     could not be parsed has none at all.
    /// </summary>
    /// <remarks>
    ///     Both read as <c>null</c>, which the client renders as a skip it makes no claim about rather than a dead one.
    /// </remarks>
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

        return new DevWorkflowRunCostResponse
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ToolCalls = toolCalls,
            ProviderCalls = providerCalls,
            AgentTurnMs = agentTurnMs
        };
    }

    private static long? Add(long? total, long? term) =>
        term is { } value ? (total ?? 0) + value : total;

    private static int? Add(int? total, int? term) =>
        term is { } value ? (total ?? 0) + value : total;

    /// <summary>
    ///     The stored route on the wire, parsed by the runtime's own reader — the one that owns the document — and only
    ///     re-shaped here.
    /// </summary>
    /// <remarks>
    ///     An unreadable column costs this node its route rather than costing the drill-down a 500, exactly as an
    ///     unreadable policy resolution does.
    /// </remarks>
    private static DevWorkflowNodeRouteResponse? Route(string? routeJson) =>
        DevWorkflowNodeRunDocuments.TryParseRoute(routeJson) is { } route
            ? new DevWorkflowNodeRouteResponse
            {
                Satisfied = route.Satisfied,
                Dead = route.Dead,
                Waived = route.Waived,
                GateAnswer = route.GateAnswer,
                Truncated = route.Truncated
            }
            : null;

    /// <summary>
    ///     Which upstream nodes a <c>Pending</c> node run is still waiting on, computed here rather than left to the
    ///     client, which would duplicate the dispatcher's own join evaluation and drift from it.
    /// </summary>
    /// <remarks>
    ///     Only <c>Pending</c> carries it — <c>Blocked</c> means a human is the dependency. A source that has SETTLED is
    ///     never waited on, whichever way it settled, which is why an <c>Any</c> join needs no case of its own. A
    ///     materialization TEMPLATE never gets a row, so it can never settle, and naming it would show every
    ///     decomposing run as stuck on the one node nothing ever runs. Fuller account:
    ///     docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
    /// </remarks>
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
    ///     and the bound agent's otherwise.
    /// </summary>
    /// <remarks>
    ///     Null when neither pins anything, since the session then takes the node's default chat model — a live setting
    ///     this pane has no business naming as if the run had chosen it. The authored pin counts only on an AGENT node
    ///     run, the one lane that dispatches on it, and the value is trimmed because the parser trims before it pins.
    ///     Which half is stable and which re-reads live: docs/wiki/09-api-and-hubs.md
    ///     ("Design notes on the newer endpoint families").
    /// </remarks>
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

        // simplified: lists every agent definition to name a handful. Definitions are few and the alternative is one
        // read per node; a name-only projection on the agent store is the upgrade if a repaint ever feels it.
        var definitions = await _agents.ListAsync(cancellationToken);
        return definitions.ToDictionary(static definition => definition.Id);
    }

    /// <summary>
    ///     Which node runs consumed an artifact that has since been superseded.
    /// </summary>
    /// <remarks>
    ///     Staleness is written by the two callers that supersede an artifact — an agent-node promotion and a Tool
    ///     node's report, both through <c>MarkDependentsStaleAsync</c> — so this answers a real question rather than a
    ///     reserved one. It is still "none" for most runs and costs one artifact read to say so: the per-node read only
    ///     happens once a stale row actually exists.
    /// </remarks>
    private async Task<IReadOnlySet<Guid>> ResolveStaleInputsAsync(Guid runId,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        var artifacts = await _queries.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken);
        var stale = artifacts.Where(static artifact => artifact.IsStale).Select(static artifact => artifact.Id).ToHashSet();
        if (stale.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // simplified: one read per node run, and only while a stale artifact exists on the run. A grouped
        // "uses joined to stale artifacts" store query is the upgrade when staleness starts being written.
        var affected = new HashSet<Guid>();
        foreach (var nodeRunId in nodeRuns.Select(static nodeRun => nodeRun.Id))
        {
            var consumed = await _queries.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken);
            if (consumed.Any(stale.Contains))
            {
                _ = affected.Add(nodeRunId);
            }
        }

        return affected;
    }

    /// <summary>
    ///     Which rule text applied, read from the record written at materialization — never re-resolved, which would
    ///     answer "what would apply now", a different and misleading question in an audit view.
    /// </summary>
    /// <remarks>
    ///     The CURRENT hash of each named rule set rides alongside it, so a reader can tell an unchanged document from
    ///     one edited since the node ran, and both from one deleted — which reads as a null current hash. Parsed
    ///     through the runtime's own tolerant reader: an unreadable column is a hand-edited row, and it must cost this
    ///     node its rule-set list rather than costing the whole drill-down a 500.
    /// </remarks>
    private static IReadOnlyList<DevWorkflowAppliedRuleSetResponse> AppliedRuleSets(string? policyResolutionJson, IReadOnlyList<DevWorkflowRuleSetSummary> current) =>
    [
        .. DevWorkflowRulePolicyResolver.Read(policyResolutionJson)
                                        .Select(applied => new DevWorkflowAppliedRuleSetResponse
                                        {
                                            Id = applied.Id,
                                            Name = applied.Name,
                                            ContentSha256 = applied.ContentSha256,
                                            CurrentContentSha256 = current.FirstOrDefault(ruleSet => ruleSet.Id == applied.Id)?.ContentSha256
                                        })
    ];
}
