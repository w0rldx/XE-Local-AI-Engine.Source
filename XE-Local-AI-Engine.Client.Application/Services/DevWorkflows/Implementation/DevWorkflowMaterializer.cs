namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     Expands a decomposition into the work it decided on: one clone of the template subtree per task, wired into the
///     join, written with the rewritten graph in ONE transaction.
///     <para>
///         The run's pinned graph is the single source of routing truth, so growing a run means rewriting that blob —
///         never the definition, which is what keeps re-running the same definition unaffected and walkthrough #9
///         atomic. The rows and the rewrite therefore commit together: a rewrite without matching rows leaves the
///         dispatcher waiting on nodes it has no row for, which HANGS rather than fails.
///     </para>
///     <para>
///         It runs LAST in a tick and the tick returns immediately afterwards, because everything downstream of it
///         would otherwise be judging the graph this call has just replaced.
///     </para>
///     <para>
///         Task creation is deliberately NOT here: a materialized child's Development task is created by the
///         implementation lane at first dispatch, so this transaction stays rows-and-graph and a crash between the two
///         leaves nothing half-created outside the run.
///     </para>
/// </summary>
internal sealed class DevWorkflowMaterializer
{
    /// <summary>
    ///     The attempt the expansion is keyed under. Not a real attempt number — those start at one — because a
    ///     decomposition expands ONCE for the life of the run: a second decomposition after a plan revision is named as
    ///     v2, and keying by attempt would quietly make the fix loop do it.
    /// </summary>
    private const int MaterializationAttempt = 0;

    /// <summary>Separates a template node's key from the task it was cloned for. Matches the layout §5.9 names.</summary>
    private const char CloneSeparator = '#';

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly DevWorkflowOptions _options;
    private readonly DevWorkflowRetryPolicy _retries;

    public DevWorkflowMaterializer(IDevWorkflowArtifactBlobStore blobs, DevWorkflowRetryPolicy retries, IOptions<DevWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _retries = retries ?? throw new ArgumentNullException(nameof(retries));
        _options = options.Value;
    }

    /// <summary>
    ///     Expands the first decomposition that has settled and not yet been expanded, and answers how many writes it
    ///     made — zero meaning there was nothing to expand, which is the ordinary answer on almost every tick.
    /// </summary>
    public async Task<int> MaterializeAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRuns);

        foreach (var producer in nodeRuns.Where(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Succeeded)
                                         .OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal))
        {
            if (graph.Nodes.GetValueOrDefault(producer.NodeKey) is not { Materialization: { } materialization } node)
            {
                continue;
            }

            // The commit marker, asked for by id rather than counted off the rows: it is the ONE answer that covers
            // both a run that grew children and one whose decomposition legitimately produced no work at all, and it
            // survives the fix loop re-running this node — a second expansion is v2, and the first one's children are
            // still the run's.
            var operationId = DevWorkflowOperationId.For(run.Id, producer.NodeKey, MaterializationAttempt, "materialize");
            if (await store.FindOperationEventTypeAsync(run.Id, operationId, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            return await ExpandAsync(store, graph, run, node, materialization, producer, nodeRuns, operationId, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> ExpandAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowMaterialization materialization,
        DevWorkflowNodeRunSnapshot producer,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var package = await ReadPackageAsync(store, run, materialization, producer, cancellationToken).ConfigureAwait(false);
        if (package.Error is { } unreadable)
        {
            return await RejectAsync(store, graph, run, producer, nodeRuns, unreadable, cancellationToken).ConfigureAwait(false);
        }

        var tasks = package.Tasks;
        if (Reject(graph, materialization, tasks, nodeRuns.Count, _options.MaxNodeRunsPerRun) is { } rejected)
        {
            return await RejectAsync(store, graph, run, producer, nodeRuns, rejected, cancellationToken).ConfigureAwait(false);
        }

        if (tasks.Count == 0)
        {
            // A decomposition may legitimately answer "there is no follow-up work". The graph is left exactly as it is
            // — the join keeps its edge from this node and fires on it — and only the marker is written, so the next
            // tick knows this decomposition is done rather than reading its artifact again forever.
            //
            // The detail says so: this is the one graph.changed that changes no graph, and a consumer that refetched
            // on the token alone would fetch the same revision back. `graphRevision` is the run's CURRENT one, which
            // has not moved.
            _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand(run.Id,
                                   DevWorkflowVersions.Any,
                                   DevWorkflowEventTypes.GraphChanged,
                                   producer.Id,
                                   operationId,
                                   DetailJson: JsonSerializer.Serialize(new ExpansionDetail(producer.NodeKey,
                                           TaskCount: 0,
                                           package.ArtifactId,
                                           run.GraphRevision,
                                           RevisionBumped: false),
                                       JsonOptions)),
                               cancellationToken)
                           .ConfigureAwait(false);
            return 1;
        }

        var expansion = Compose(graph, run.GraphJson, node, materialization, tasks);

        // The producer's route, RE-taken against the graph this expansion writes and carried into the same transaction
        // as the rewrite. Its route was recorded when the node settled, before the clone-root edges existed — so left
        // alone the persisted document lists the authored join edge and omits every root the next tick actually admits,
        // which is a recorded route disagreeing with the routing that happened. No gate answer to record: a node
        // carrying a materialization is never a HumanGate.
        var producerRoute = DevWorkflowStateMachine.RouteJson(DevWorkflowStateMachine.RouteTaken(DevWorkflowGraph.Parse(expansion.GraphJson),
            producer,
            nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal),
            decision: null));

        // Read once for this expansion, after the decision to expand has been made: every clone's resolution comes off
        // the same list, and a tick that expands nothing never touches the table at all.
        var enabledRuleSets = await store.ListEnabledRuleSetsAsync(cancellationToken).ConfigureAwait(false);
        _ = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(run.Id,
                               DevWorkflowVersions.Any,
                               operationId,
                               [
                                   .. expansion.Clones.Select(clone => new DevWorkflowNodeRunSeed(Guid.NewGuid(),
                                       clone.NodeKey,
                                       clone.Node.NodeType,
                                       clone.Node.MaxAttempts,
                                       clone.Node.AgentDefinitionId,
                                       producer.DevelopmentProjectId,
                                       clone.InputJson,

                                       // The clone inherits the producer's project, so it resolves against the same
                                       // project axis its parent did — and against its OWN node type, which is what
                                       // makes a rule set scoped to Tool nodes reach a materialized Tool clone.
                                       DevWorkflowRulePolicyResolver.Compose(enabledRuleSets, producer.DevelopmentProjectId, clone.Node.NodeType),
                                       producer.Id,
                                       clone.Index))
                               ],
                               expansion.GraphJson,
                               producer.Id,
                               producerRoute),
                           cancellationToken)
                       .ConfigureAwait(false);
        return expansion.Clones.Count;
    }

    /// <summary>
    ///     Stands the decomposition down over output it produced but nothing can use.
    ///     <para>
    ///         Through the ordinary retry policy, so the node's first answer to malformed output is another attempt
    ///         carrying the schema error in its objective — the cheapest correction loop available, since the thing that
    ///         wrote the document is the thing that can fix it — and the answer when that is spent is a human.
    ///     </para>
    ///     <para>
    ///         Two things the stand-down does to a row that had SUCCEEDED, both deliberate. Its <c>EndedAtUtc</c> is
    ///         the success's, and is left alone: the agent really did finish then, and re-stamping it would date the
    ///         node's work to the moment its output was judged. And its <c>OutputJson</c> is REPLACED by the refusal,
    ///         losing the document the node panel had been rendering — accepted, because the panel's job is to explain
    ///         the row's current state, that state is Blocked, and the reason it is blocked is the more useful of the
    ///         two answers. The promoted artifact still holds what the node actually produced.
    ///     </para>
    /// </summary>
    private async Task<int> RejectAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot producer,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        string reason,
        CancellationToken cancellationToken) =>
        await _retries.SettleFailureAsync(store,
                          graph,
                          run,
                          producer,
                          nodeRuns,
                          new DevWorkflowFailure(DevWorkflowFailureClasses.Configuration,
                              reason,
                              JsonSerializer.Serialize(new RejectedOutput(DevWorkflowNodeOutputStatuses.Failed,
                                      producer.Attempt,
                                      DevWorkflowFailureClasses.Configuration,
                                      reason),
                                  JsonOptions)),
                          cancellationToken)
                      .ConfigureAwait(false);

    /// <summary>
    ///     The newest task package this node produced, parsed — or the sentence an operator and the next attempt are
    ///     both told.
    ///     <para>
    ///         <b>Deliberately not attempt-scoped, and this is the one place that reading is right.</b> Artifacts are
    ///         run-scoped, so a re-attempt that saved nothing leaves attempt 1's package the newest — and judging
    ///         attempt 2 on it is the correct answer here, because the package IS the node's output: an attempt that
    ///         produced no new one has not corrected anything, and the second refusal is what stands the node down for
    ///         a human. Do not "fix" this by keying on the attempt. It is the shape F5's HIGH-1 got wrong for a node
    ///         PANEL — where showing a previous attempt's evidence as current is a lie — reaching the opposite verdict
    ///         here for the same reason: there, the question is "what did this attempt do"; here, it is "is there a
    ///         usable package on this run yet".
    ///     </para>
    /// </summary>
    private async Task<TaskPackage> ReadPackageAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowMaterialization materialization,
        DevWorkflowNodeRunSnapshot producer,
        CancellationToken cancellationToken)
    {
        var artifacts = await store.ListArtifactsAsync(run.Id, sinceSequence: 0, cancellationToken).ConfigureAwait(false);
        var artifact = artifacts.Where(entry => string.Equals(entry.ProducingNodeKey, producer.NodeKey, StringComparison.Ordinal)
                                                && entry.Kind == materialization.ArtifactKind
                                                && entry is { IsValid: true, IsLatest: true })
                                .MaxBy(static entry => entry.Sequence);
        if (artifact is null)
        {
            return TaskPackage.Rejected($"Node '{producer.NodeKey}' decomposes the work, but produced no {materialization.ArtifactKind} artifact to decompose it from.");
        }

        var read = await _blobs.ReadAsync(run.Id, artifact.Id, artifact.ContentSha256, artifact.SizeBytes, cancellationToken).ConfigureAwait(false);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            return TaskPackage.Rejected($"The {materialization.ArtifactKind} artifact '{artifact.Name}' did not read back ({read.Status}), so there is nothing to decompose.");
        }

        return Parse(Encoding.UTF8.GetString(read.Content.Span), artifact.Id, artifact.Name);
    }

    /// <summary>
    ///     The fixed §5.9 schema: an array of <c>{ id, title, goal, allowedPaths[], dependsOn[], acceptanceCriteria[] }</c>,
    ///     at the root or under a <c>tasks</c> property — a model writing an object around its list is the commonest
    ///     shape of the same answer, and refusing it would spend a whole re-attempt on punctuation.
    /// </summary>
    private static TaskPackage Parse(string content, Guid artifactId, string name)
    {
        IReadOnlyList<TaskPackageItem>? items;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array => tasks,
                _ => default
            };

            items = array.ValueKind == JsonValueKind.Array ? array.Deserialize<List<TaskPackageItem>>(JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return TaskPackage.Rejected($"The task package '{name}' is not valid JSON: {exception.Message}");
        }

        if (items is null)
        {
            return TaskPackage.Rejected($"The task package '{name}' must be an array of tasks, or an object with a 'tasks' array.");
        }

        // A JSON `null` in the array deserializes to a null ELEMENT despite the non-nullable annotation, and every
        // reader below here — the refusal table, the composer — dereferences the entry. Refused at the parse boundary
        // rather than in one of them, because this is the one place that makes `Tasks` element-non-null by
        // construction: reaching any reader with the hole throws out of the tick, and a tick that throws over a
        // decomposition that has already SUCCEEDED re-throws on every tick after it. That is the wedge this module
        // refuses to have; the stand-down is a refusal the node can be told about instead.
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is null)
            {
                return TaskPackage.Rejected($"The task package '{name}' has nothing at position {index + 1} where a task should be; every entry must be an object describing one task.");
            }
        }

        return new TaskPackage([.. items], artifactId, Error: null);
    }

    /// <summary>
    ///     Every reason a well-formed package is still refused, in the order that names the smallest cause first.
    ///     <para>
    ///         All of them are answered the same way — the node that wrote the package is asked to write it again with
    ///         the complaint in its objective — so what matters here is that the sentence is specific enough for a model
    ///         (and a human reading the same field) to act on.
    ///     </para>
    /// </summary>
    private static string? Reject(DevWorkflowGraph graph,
        DevWorkflowMaterialization materialization,
        IReadOnlyList<TaskPackageItem> tasks,
        int existingNodeRuns,
        int maxNodeRunsPerRun)
    {
        if (tasks.Count > materialization.MaxChildren)
        {
            return $"The task package names {tasks.Count} tasks, more than the {materialization.MaxChildren} this decomposition allows.";
        }

        var subtree = graph.TemplateSubtree(materialization);
        if (existingNodeRuns + (tasks.Count * subtree.Count) > maxNodeRunsPerRun)
        {
            return $"Expanding {tasks.Count} tasks would take this run past the {maxNodeRunsPerRun} node runs it may carry.";
        }

        // Whether the template carries a DevTask ANYWHERE decides whether a task has to name the files it changes: that
        // is the node type whose clone becomes a Development coder attempt, and the "must export a patch" contract is
        // the attempt's, not the decomposition's. The whole subtree rather than its root, because a custom template is
        // free to root itself in an Agent that briefs a DevTask below it — and there the coder that cannot finish on an
        // empty patch is exactly as real, just one node further down. Read once, because it cannot differ between tasks
        // of one package.
        var subtreeHasDevTask = graph.TemplateSubtreeHasDevTask(materialization);

        var ids = new HashSet<string>(StringComparer.Ordinal);

        // Every clone key this package WOULD take, against the task that first claimed it. Built as the loop goes,
        // because the collision that wedges a run is as easily between two tasks of one package as with an existing
        // node: "{nodeKey}#{taskId}" is not injective — a template that carries both `a` and `a#b` generates `a#b#c`
        // for task `b#c` and again for task `c` — and the store's unique (run_id, node_key) answers that with a refused
        // insert, which throws out of the tick instead of standing the decomposition down.
        var generated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Id))
            {
                return "Every task in the package needs a non-empty 'id', which is what names the node runs it becomes.";
            }

            if (string.IsNullOrWhiteSpace(task.Goal))
            {
                return $"Task '{task.Id}' names no 'goal', so there would be nothing for it to implement.";
            }

            if (!ids.Add(task.Id))
            {
                return $"The task package names '{task.Id}' twice, and two tasks cannot share one identity.";
            }

            // A template carrying a DevTask only. There a task becomes a Development coder attempt, and that attempt
            // cannot finish without exporting a NON-EMPTY patch: a slice with nothing to change — a survey, a style
            // profile, a verification — is refused, re-attempted twice more, refused twice more, and then blocks the
            // run in front of a human. Live, four runs went that way. 'changes' is the one signal the package carries
            // that there IS something to change, so a package that names none is handed straight back to the node that
            // wrote it, while it is still cheap to fix. A template with no DevTask in it keeps the old contract: its
            // clones are ordinary sessions with no patch to export and nothing this would be judging.
            if (subtreeHasDevTask)
            {
                var changes = (task.Changes ?? []).Where(static change => !string.IsNullOrWhiteSpace(change)).ToList();
                if (changes.Count == 0)
                {
                    return $"Task '{task.Id}' names no file it will add or edit in 'changes', so there is nothing for a coder to implement. "
                           + "A task must change code; fold reading or surveying into the task that needs it.";
                }

                // The workspace confinement the coder's own tools enforce, asked here instead: an absolute path, one
                // that climbs out of the workspace, or one under protected Git state is a file the coder would be
                // refused for touching, so the decomposition is told now rather than three attempts later.
                if (changes.Find(static change => !DevelopmentWorkspaceSecurity.Confine(change, allowRoot: false).IsAccepted) is { } unusable)
                {
                    return $"Task '{task.Id}' names '{unusable}' in 'changes', which is not a file a coder could touch: "
                           + "every entry must be a path relative to the repository root, with no leading slash, no '..' above it and nothing under '.git'.";
                }
            }

            // Not enforced anywhere yet: the child brief carries the title, the requirements and the acceptance
            // criteria, and Dev Mode's workspace policy has no per-task path restriction to hand this to (Slice D). A
            // decomposition that leans on it for parallel-child isolation would get none, silently, so it is refused
            // loudly instead. The field stays in the schema and on the stored artifact — this refuses a package that
            // DEPENDS on it, not one that mentions it.
            if (task.AllowedPaths is { Count: > 0 })
            {
                return $"Task '{task.Id}' restricts itself to specific paths with 'allowedPaths', which this version does not enforce. "
                       + "Remove the field and describe the boundary in the goal instead.";
            }

            // EVERY node of the subtree, not just its root: a graph that happens to declare a node named like one of
            // the other clones collides just as hard, and the collision surfaces at the store as a refused insert —
            // which throws out of the tick and wedges the run rather than standing this node down.
            foreach (var key in subtree.Select(key => CloneKey(key, task.Id)))
            {
                if (graph.Nodes.ContainsKey(key))
                {
                    return $"Task '{task.Id}' would take the node key '{key}', which this run already carries.";
                }

                if (!generated.TryAdd(key, task.Id))
                {
                    return $"Tasks '{generated[key]}' and '{task.Id}' would both take the node key '{key}', and two node runs cannot share one key.";
                }
            }
        }

        if (tasks.SelectMany(static task => task.DependsOn ?? []).FirstOrDefault(dependency => !ids.Contains(dependency)) is { } unknown)
        {
            return $"A task depends on '{unknown}', which the package does not declare.";
        }

        return HasCycle(tasks) ? "The tasks depend on each other in a cycle, so none of them could ever start." : null;
    }

    /// <summary>Depth-first colouring over <c>dependsOn</c>, the same shape the graph's own acyclicity check uses.</summary>
    private static bool HasCycle(IReadOnlyList<TaskPackageItem> tasks)
    {
        var byId = tasks.ToDictionary(static task => task.Id!, StringComparer.Ordinal);
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        return tasks.Any(task => Walk(task.Id!));

        bool Walk(string id)
        {
            if (finished.Contains(id))
            {
                return false;
            }

            if (!onPath.Add(id))
            {
                return true;
            }

            var cycle = (byId[id].DependsOn ?? []).Any(Walk);
            _ = onPath.Remove(id);
            _ = finished.Add(id);
            return cycle;
        }
    }

    /// <summary>
    ///     The rewritten graph and the rows that go with it, composed together so the two cannot disagree.
    ///     <para>
    ///         The rewrite works on the stored JSON rather than on the parsed projection: the projection keeps only what
    ///         the runtime routes on, and re-serialising it would silently drop every authoring field the editor put
    ///         there. So each clone is the template node's own JSON with its key — and any retry target — rewritten.
    ///     </para>
    /// </summary>
    private static Expansion Compose(DevWorkflowGraph graph,
        string graphJson,
        DevWorkflowGraphNode node,
        DevWorkflowMaterialization materialization,
        IReadOnlyList<TaskPackageItem> tasks)
    {
        var subtree = graph.TemplateSubtree(materialization);
        var root = JsonNode.Parse(graphJson)!.AsObject();
        var nodes = root["nodes"]!.AsArray();
        var edges = root["edges"] as JsonArray;
        if (edges is null)
        {
            edges = [];
            root["edges"] = edges;
        }

        var templates = nodes.OfType<JsonObject>()
                             .Where(entry => subtree.Contains(entry["nodeKey"]!.GetValue<string>()))
                             .ToDictionary(static entry => entry["nodeKey"]!.GetValue<string>(), StringComparer.Ordinal);

        // The authored edges by endpoint, so a cloned branch carries the condition its author wrote rather than losing
        // it to the projection, which parses conditions into a form this cannot write back.
        var conditions = edges.OfType<JsonObject>()
                              .Where(static edge => edge["condition"] is not null)
                              .ToDictionary(static edge => (edge["from"]!.GetValue<string>(), edge["to"]!.GetValue<string>()));

        // A leaf of the template is what the join is actually waiting for. Computed from the template's OWN edges, so a
        // template that already names the join keeps that edge and one that names nothing gets it — either way the join
        // waits for every task's last node rather than firing while they run.
        //
        // ponytail: an `Any` join is left to the author. One task expanding into an `Any` join gives it a single live
        // inbound edge, which parse refuses — the run then FAILS as unroutable with that sentence rather than hanging,
        // and the seeded templates all join with `All`. Upgrade path, if a template ever wants it: relax the two-edge
        // rule for a join a materialization names, since its real width is only known once the package is read.
        var leaves = subtree.Where(key => !graph.OutboundEdges(key).Any(edge => subtree.Contains(edge.To))).ToList();
        var clones = new List<Clone>();
        var wired = new HashSet<(string From, string To)>();
        foreach (var (task, index) in tasks.Select(static (task, index) => (task, index + 1)))
        {
            var brief = JsonSerializer.Serialize(new DevTaskBrief(Present(task.Title), RequirementsFor(task), Criteria(task.AcceptanceCriteria)), JsonOptions);
            foreach (var key in subtree.OrderBy(static key => key, StringComparer.Ordinal))
            {
                var clone = (JsonObject)templates[key].DeepClone();
                clone["nodeKey"] = CloneKey(key, task.Id!);
                if (clone["retryTarget"]?.GetValue<string>() is { } retryTarget && subtree.Contains(retryTarget))
                {
                    clone["retryTarget"] = CloneKey(retryTarget, task.Id!);
                }

                nodes.Add(clone);
                clones.Add(new Clone(CloneKey(key, task.Id!), graph.Nodes[key], brief, index));

                foreach (var edge in graph.OutboundEdges(key).Where(edge => subtree.Contains(edge.To)))
                {
                    Wire(CloneKey(key, task.Id!), CloneKey(edge.To, task.Id!), conditions.GetValueOrDefault((edge.From, edge.To))?["condition"]?.DeepClone());
                }
            }

            foreach (var leaf in leaves)
            {
                Wire(CloneKey(leaf, task.Id!), materialization.JoinNodeKey, condition: null);
            }

            foreach (var dependency in task.DependsOn ?? [])
            {
                foreach (var leaf in leaves)
                {
                    Wire(CloneKey(leaf, dependency), CloneKey(materialization.TemplateNodeKey, task.Id!), condition: null);
                }
            }

            if ((task.DependsOn ?? []).Count == 0)
            {
                Wire(node.NodeKey, CloneKey(materialization.TemplateNodeKey, task.Id!), condition: null);
            }
        }

        // The decomposition's OWN edge into the join is kept, and that is a fix rather than an oversight. It was removed
        // here on the reading that a join left waiting on an already-Succeeded node would fire the moment the
        // decomposition landed — which admission does not do: `All` waits while any inbound edge is Pending, and so
        // does `Any`, so the clones' fresh edges hold the join exactly as they did before. What removing it DID do was
        // take the decomposition off every path back from the join, and upstream artifact resolution walks those paths:
        // the node behind the join was left inheriting the clones' validation reports and nothing else, so the run's
        // verification agent judged the feature without the task package it was decomposed into. Live, it said so
        // itself and returned "not yet".
        return new Expansion(clones, root.ToJsonString(JsonOptions));

        void Wire(string from, string to, JsonNode? condition)
        {
            if (!wired.Add((from, to)))
            {
                return;
            }

            var edge = new JsonObject
            {
                ["from"] = from,
                ["to"] = to
            };
            if (condition is not null)
            {
                edge["condition"] = condition;
            }

            edges.Add(edge);
        }
    }

    private static string CloneKey(string nodeKey, string taskId) =>
        $"{nodeKey}{CloneSeparator}{taskId}";

    /// <summary>
    ///     What the coder is told to implement: the task's goal, and the files the decomposition said it would touch.
    ///     <para>
    ///         Folded into the requirements rather than carried as a field of its own, because <c>requirements</c> is
    ///         the whole of what the implementation lane renders to the coder — a second field would have to be
    ///         threaded through the brief, the executor's own reader and the Development task before it reached a
    ///         prompt, to say something a sentence says here. It is guidance, not a boundary: nothing refuses a coder
    ///         for touching a file this does not name.
    ///     </para>
    /// </summary>
    private static string RequirementsFor(TaskPackageItem task)
    {
        var changes = (task.Changes ?? []).Where(static change => !string.IsNullOrWhiteSpace(change)).ToList();
        return changes.Count == 0
            ? task.Goal!
            : string.Concat(task.Goal, Environment.NewLine, Environment.NewLine, "Files this task will add or edit: ", string.Join(", ", changes));
    }

    private static string? Present(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The task's acceptance criteria as the JSON array Dev Mode stores, or nothing when it named none.</summary>
    private static string? Criteria(IReadOnlyList<string>? acceptanceCriteria) =>
        acceptanceCriteria is { Count: > 0 } criteria ? JsonSerializer.Serialize(criteria, JsonOptions) : null;

    /// <summary>One task of the §5.9 package. Every member is optional at the parser; <see cref="Reject" /> says which are not.</summary>
    private sealed record TaskPackageItem(
        string? Id,
        string? Title,
        string? Goal,
        IReadOnlyList<string>? Changes,
        IReadOnlyList<string>? AllowedPaths,
        IReadOnlyList<string>? DependsOn,
        IReadOnlyList<string>? AcceptanceCriteria);

    /// <summary>A read package: its tasks and the artifact they came from, or the reason there are none.</summary>
    private sealed record TaskPackage(IReadOnlyList<TaskPackageItem> Tasks, Guid ArtifactId, string? Error)
    {
        public static TaskPackage Rejected(string error) =>
            new([], Guid.Empty, error);
    }

    /// <summary>One node run to create: its new key, the template node it copies, and the brief its task carries.</summary>
    private sealed record Clone(string NodeKey, DevWorkflowGraphNode Node, string InputJson, int Index);

    private sealed record Expansion(IReadOnlyList<Clone> Clones, string GraphJson);

    /// <summary>
    ///     What a materialized child is told to implement — the seam the implementation lane reads, whose
    ///     <c>requirements</c> is mandatory there and so is written from the task's <c>goal</c> here.
    /// </summary>
    private sealed record DevTaskBrief(string? Title, string Requirements, string? AcceptanceCriteriaJson);

    /// <summary>What the commit marker carries when there is no expansion to describe: which node, off what, and that the graph did not move.</summary>
    private sealed record ExpansionDetail(string NodeKey, int TaskCount, Guid SourceArtifactId, int GraphRevision, bool RevisionBumped);

    /// <summary>A decomposition whose own output it cannot use. The reason travels into the next attempt's objective.</summary>
    private sealed record RejectedOutput(string Status, int Attempt, string FailureClass, string MaterializationError);
}
