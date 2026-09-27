namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The Development-Workflow READ endpoints' only door onto <see cref="IDevWorkflowStore" />: the run list, the
///     paged event log, the artifact feed and the composed run view.
/// </summary>
/// <remarks>
///     Read-only by construction: commands live on <see cref="IDevWorkflowRunService" />, authoring reads on
///     <see cref="DevWorkflowAuthoringService" />. The 404-first read, the run-ownership check and the size ceiling stay
///     in the endpoints. The run view is THE repaint fetch, so <see cref="GetRunViewAsync" /> runs a fixed query budget,
///     never a per-node one; every derived field is explained in docs/wiki/09-api-and-hubs.md ("Design notes on the
///     newer endpoint families").
/// </remarks>
public sealed class DevWorkflowRunQueryService
{
    /// <summary>The same lenient read the wire graph is deserialized with, so both see the same nodes and edges.</summary>
    private static readonly JsonSerializerOptions GraphOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentDefinitionService _agents;
    private readonly IWorkSessionService _sessions;
    private readonly IDevWorkflowStore _store;

    public DevWorkflowRunQueryService(IDevWorkflowStore store, IAgentDefinitionService agents, IWorkSessionService sessions)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(sessions);
        _store = store;
        _agents = agents;
        _sessions = sessions;
    }

    /// <summary>The run list, newest first, with each run's definition name and node counters. Both filters optional.</summary>
    public Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return _store.ListRunSummariesAsync(workItemId, status, limit, cancellationToken);
    }

    /// <summary>One run row. The feeds read it first so an unknown run answers 404 rather than an empty page.</summary>
    public Task<DevWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return _store.GetRunAsync(runId, cancellationToken);
    }

    /// <summary>The run's event log from an exclusive watermark. Sequences are strictly increasing but NOT contiguous.</summary>
    public Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _store.ListEventsAsync(runId, sinceSequence, limit, cancellationToken);
    }

    /// <summary>The run's artifacts, every version of every lineage. Append-correct only: a staleness flip never advances the cursor.</summary>
    public Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default)
    {
        return _store.ListArtifactsAsync(runId, sinceSequence, cancellationToken);
    }

    /// <summary>One artifact ROW — its recorded size, hash and media type. The bytes come from the blob store, not from here.</summary>
    public Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        return _store.GetArtifactAsync(artifactId, cancellationToken);
    }

    /// <summary>A run with its node runs and every field the run view derives server-side, from a detail already read.</summary>
    public async Task<DevWorkflowRunView> GetRunViewAsync(DevWorkflowRunDetail detail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var run = detail.Run;
        var graph = ReadGraph(run.GraphJson);
        var nodesByKey = graph.Nodes!.ToDictionary(static node => node.NodeKey, StringComparer.Ordinal);
        var keysByNodeRunId = detail.NodeRuns.ToDictionary(static nodeRun => nodeRun.Id, static nodeRun => nodeRun.NodeKey);
        var byKey = detail.NodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        var agentsById = await ResolveAgentsAsync(detail.NodeRuns, cancellationToken);

        // Staleness is written by the two callers that supersede an artifact (an agent-node promotion and a Tool node's
        // report, both through MarkDependentsStaleAsync), so this is one grouped read, never one per node run.
        var staleInputs = (await _store.ListNodeRunIdsWithStaleInputsAsync(run.Id, cancellationToken)).ToHashSet();

        var definitions = await _store.ListDefinitionsAsync(includeArchived: true, cancellationToken);
        var definitionName = definitions.FirstOrDefault(definition => definition.Id == run.DefinitionId)?.Name;

        // Asked of the runtime's parser, the same answer the wire graph's isTemplate carries: one parse for the run,
        // not one per node run, and not a second walk that could disagree with the one the dispatcher admits by.
        var templates = DevWorkflowGraphContract.TemplateNodeKeys(run.GraphJson);

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
                          .Select(nodeRun =>
                          {
                              var node = nodesByKey.GetValueOrDefault(nodeRun.NodeKey);
                              return new DevWorkflowNodeRunView
                              {
                                  NodeRun = nodeRun,
                                  WaitingOnNodeKeys = WaitingOnNodeKeys(nodeRun, graph, byKey, templates),
                                  MaterializedFromNodeKey = nodeRun.MaterializedFromNodeRunId is { } parent ? keysByNodeRunId.GetValueOrDefault(parent) : null,
                                  MaterializationCount = nodeRun.MaterializedFromNodeRunId is { } group && materializationCounts.TryGetValue(group, out var count)
                                      ? count
                                      : null,
                                  AgentDisplayName = AgentDisplayName(nodeRun, node, agentsById),
                                  ModelLabel = ModelLabel(nodeRun, node, agentsById),
                                  HasStaleInputs = staleInputs.Contains(nodeRun.Id),
                                  OperatorRetries = OperatorRetries(nodeRun, declaredCaps),
                                  SkipWaived = SkipWaived(nodeRun, waivedSkips),
                                  // Asked of the contract, not of a spelling of the token repeated here: the same verdict decides the
                                  // drill-down's note, this row's badge and whether the run header counts the row as work.
                                  ValidationNotApplicable = DevWorkflowGraphContract.ValidationWasNotApplicable(nodeRun.OutputJson)
                              };
                          })
                          .ToList();

        return new DevWorkflowRunView
        {
            Run = run,
            DefinitionName = definitionName,
            Nodes = nodes,
            QueuedNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Queued),
            RunningNodeCount = detail.NodeRuns.Count(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Running),
            PendingDecisionCount = detail.PendingDecisionCount,
            BlockingGateNodeRunId = detail.BlockingGateNodeRunId,
            // Summed over the node runs already loaded above: the rollup costs no extra query, and a run's own row
            // carries no cost of its own to disagree with.
            Cost = RunCost(detail.NodeRuns)
        };
    }

    /// <summary>One node run's drill-down, read through the run it belongs to.</summary>
    /// <exception cref="DevWorkflowNotFoundException">The run or node run is unknown, or the node run belongs to another run.</exception>
    public async Task<DevWorkflowNodeRunDetailView> GetNodeRunViewAsync(Guid runId, Guid nodeRunId, CancellationToken cancellationToken = default)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken);
        var nodeRun = await _store.GetNodeRunAsync(nodeRunId, cancellationToken);
        if (nodeRun.RunId != runId)
        {
            // Reads as absent rather than as another run's node, so one run's route can never surface another's rows.
            throw new DevWorkflowNotFoundException($"Development workflow node run '{nodeRunId}' was not found on run '{runId}'.");
        }

        var node = ReadGraph(run.GraphJson).Nodes!.FirstOrDefault(entry => string.Equals(entry.NodeKey, nodeRun.NodeKey, StringComparison.Ordinal));
        var agentsById = await ResolveAgentsAsync([nodeRun], cancellationToken);

        var artifacts = await _store.ListArtifactsAsync(runId, sinceSequence: 0, cancellationToken);
        var produced = artifacts.Where(artifact => artifact.ProducedByNodeRunId == nodeRunId).OrderBy(static artifact => artifact.Sequence).ToList();
        var consumed = await _store.ListConsumedArtifactIdsAsync(nodeRunId, cancellationToken);
        var decisions = await _store.ListDecisionsAsync(runId, cancellationToken);

        // One list, and only when this node actually recorded a resolution: rule sets are a handful of bodyless rows,
        // so listing them beats a lookup per recorded id, and a node with no policy pays nothing at all.
        var ruleSets = nodeRun.PolicyResolutionJson is null
            ? []
            : await _store.ListRuleSetsAsync(cancellationToken);

        // Read from the other family on the loose session id, never stored here: a purged session leaves the node run
        // intact and the drill-down renders "transcript no longer available" instead of a broken link.
        Guid? conversationId = null;
        if (nodeRun is { WorkSessionId: { } sessionId, WorkSessionAvailable: true })
        {
            conversationId = (await _sessions.GetAsync(sessionId, cancellationToken)).ConversationId;
        }

        return new DevWorkflowNodeRunDetailView
        {
            Run = run,
            NodeRun = nodeRun,
            ConversationId = conversationId,
            AgentDisplayName = AgentDisplayName(nodeRun, node, agentsById),
            ModelLabel = ModelLabel(nodeRun, node, agentsById),
            // The node's headline output: the newest version it produced, which is the one a review panel opens.
            PrimaryArtifactId = produced.LastOrDefault(static artifact => artifact.IsLatest)?.Id ?? produced.LastOrDefault()?.Id,
            ProducedArtifactIds = [.. produced.Select(static artifact => artifact.Id)],
            ConsumedArtifactIds = consumed,
            AppliedRuleSets = AppliedRuleSets(nodeRun.PolicyResolutionJson, ruleSets),
            AllowedDecisions = DevWorkflowGraphContract.AllowedDecisions(nodeRun.Status),
            // Only a human gate produces the answer an out-edge condition reads, so only there does the question mean
            // anything. False here is what tells the confirm dialog that a rejection ENDS the run.
            HasRejectBranch = nodeRun.NodeType == DevWorkflowNodeType.HumanGate && DevWorkflowGraphContract.HasRejectBranch(run.GraphJson, nodeRun.NodeKey),
            Decisions = [.. decisions.Where(decision => decision.NodeRunId == nodeRunId).OrderBy(static decision => decision.Sequence)],
            OperatorRetries = OperatorRetries(nodeRun, DevWorkflowGraphContract.DeclaredMaxAttempts(run.GraphJson))
        };
    }

    /// <summary>
    ///     The pinned graph's nodes and edges as the wire graph reads them: the same lenient deserialize, so a graph the
    ///     parser cannot route still draws, and a node's waiting-on keys and agent come off the edges it is drawn with.
    /// </summary>
    private static ViewGraph ReadGraph(string graphJson)
    {
        var graph = JsonSerializer.Deserialize<ViewGraph>(graphJson, GraphOptions) ?? new ViewGraph();
        return new ViewGraph
        {
            Nodes = graph.Nodes ?? [],
            Edges = graph.Edges ?? []
        };
    }

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
    private static DevWorkflowRunCost RunCost(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
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

        return new DevWorkflowRunCost
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
        ViewGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey,
        IReadOnlySet<string> templates)
    {
        if (nodeRun.Status != DevWorkflowNodeRunStatus.Pending)
        {
            return null;
        }

        var waiting = graph.Edges!.Where(edge => string.Equals(edge.To, nodeRun.NodeKey, StringComparison.Ordinal))
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
        ViewNode? node,
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
        ViewNode? node,
        IReadOnlyDictionary<Guid, AgentDefinitionRecord> agentsById) =>
        PinnedModel(nodeRun, node)
        ?? (nodeRun.AgentDefinitionId is { } id && agentsById.TryGetValue(id, out var agent) ? agent.ModelProfile : null);

    /// <summary>The node's own pin as the RUNTIME will read it — trimmed, blank treated as absent — and only where a node run dispatches on one.</summary>
    private static string? PinnedModel(DevWorkflowNodeRunSnapshot nodeRun, ViewNode? node) =>
        nodeRun.NodeType == DevWorkflowNodeType.Agent && node?.ModelProfile?.Trim() is { Length: > 0 } pinned ? pinned : null;

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
    private static IReadOnlyList<DevWorkflowAppliedRuleSetView> AppliedRuleSets(string? policyResolutionJson, IReadOnlyList<DevWorkflowRuleSetSummary> current) =>
    [
        .. DevWorkflowRulePolicyResolver.Read(policyResolutionJson)
                                        .Select(applied => new DevWorkflowAppliedRuleSetView
                                        {
                                            Id = applied.Id,
                                            Name = applied.Name,
                                            ContentSha256 = applied.ContentSha256,
                                            CurrentContentSha256 = current.FirstOrDefault(ruleSet => ruleSet.Id == applied.Id)?.ContentSha256
                                        })
    ];

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

    /// <summary>The slice of the stored graph document the run view reads: node keys, agent bindings and edges.</summary>
    internal sealed class ViewGraph
    {
        public IReadOnlyList<ViewNode>? Nodes { get; init; }

        public IReadOnlyList<ViewEdge>? Edges { get; init; }
    }

    internal sealed class ViewNode
    {
        public string NodeKey { get; init; } = null!;

        public string? AgentSeedSlug { get; init; }

        public string? ModelProfile { get; init; }
    }

    internal sealed class ViewEdge
    {
        public string From { get; init; } = null!;

        public string To { get; init; } = null!;
    }
}
