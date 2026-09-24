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
/// </summary>
/// <remarks>
///     The run's pinned graph is the single source of routing truth, so growing a run rewrites that blob and never
///     the definition. Rows and rewrite commit together: a rewrite without matching rows leaves the dispatcher
///     waiting on nodes it has no row for, which HANGS rather than fails. It runs LAST in a tick and the tick
///     returns straight after, because everything downstream would judge the graph this call just replaced. See
///     docs/wiki/25-dev-workflows.md ("Decomposition and materialization").
/// </remarks>
internal sealed class DevWorkflowMaterializer
{
    /// <summary>
    ///     The attempt the expansion is keyed under. Not a real attempt number, because a decomposition expands ONCE
    ///     for the life of the run.
    /// </summary>
    /// <remarks>
    ///     Real attempts start at one. A second decomposition after a plan revision is named as v2, and keying by
    ///     attempt would quietly make the fix loop do it.
    /// </remarks>
    private const int MaterializationAttempt = 0;

    /// <summary>Separates a template node's key from the task it was cloned for. Matches the clone-key layout the task package defines.</summary>
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

            // The commit marker, asked for by id rather than counted off the rows: it is the ONE answer covering both a run that grew children and one whose decomposition legitimately
            // produced no work, and it survives the fix loop re-running this node — a second expansion is v2, and the first one's children are still the run's.
            var operationId = DevWorkflowOperationId.For(run.Id, producer.NodeKey, MaterializationAttempt, "materialize");
            if (await store.FindOperationEventTypeAsync(run.Id, operationId, cancellationToken) is not null)
            {
                continue;
            }

            return await ExpandAsync(store, graph, run, node, materialization, producer, nodeRuns, operationId, cancellationToken);
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
        var package = await ReadPackageAsync(store, run, materialization, producer, cancellationToken);
        if (package.Error is { } unreadable)
        {
            return await RejectAsync(store, graph, run, producer, nodeRuns, unreadable, cancellationToken);
        }

        var tasks = package.Tasks;
        if (Reject(graph, materialization, tasks, nodeRuns.Count, _options.MaxNodeRunsPerRun) is { } rejected)
        {
            return await RejectAsync(store, graph, run, producer, nodeRuns, rejected, cancellationToken);
        }

        if (tasks.Count == 0)
        {
            return await NothingToDoAsync(store, graph, run, materialization, producer, package.ArtifactId, operationId, cancellationToken);
        }

        var expansion = Compose(graph, run.GraphJson, node, materialization, tasks);

        // The producer's route, RE-taken against the graph this expansion writes and carried into the same transaction as the rewrite. Its route was recorded when the node settled,
        // before the clone-root edges existed, so left alone the document would list the authored join edge and omit every root the next tick admits. No gate answer: never a HumanGate.
        var producerRoute = DevWorkflowStateMachine.RouteJson(DevWorkflowStateMachine.RouteTaken(DevWorkflowGraph.Parse(expansion.GraphJson),
            producer,
            nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal),
            decision: null));

        // Read once for this expansion, after the decision to expand: every clone's resolution comes off the same list, and a tick that expands nothing never touches the table.
        var enabledRuleSets = await store.ListEnabledRuleSetsAsync(cancellationToken);
        _ = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand
            {
                RunId = run.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                OperationId = operationId,
                NodeRuns =
                [
                    .. expansion.Clones.Select(clone => new DevWorkflowNodeRunSeed
                    {
                        NodeRunId = Guid.NewGuid(),
                        NodeKey = clone.NodeKey,
                        NodeType = clone.Node.NodeType,
                        MaxAttempts = clone.Node.MaxAttempts,
                        AgentDefinitionId = clone.Node.AgentDefinitionId,
                        DevelopmentProjectId = producer.DevelopmentProjectId,
                        InputJson = clone.InputJson,
                        // The clone inherits the producer's project, resolving against the same project axis its parent did — and against its OWN node
                        // type, which is what makes a rule set scoped to Tool nodes reach a materialized Tool clone.
                        PolicyResolutionJson = DevWorkflowRulePolicyResolver.Compose(enabledRuleSets, producer.DevelopmentProjectId, clone.Node.NodeType),
                        MaterializedFromNodeRunId = producer.Id,
                        MaterializationIndex = clone.Index
                    })
                ],
                GraphJson = expansion.GraphJson,
                RouteNodeRunId = producer.Id,
                RouteJson = producerRoute
            },
            cancellationToken);
        return expansion.Clones.Count;
    }

    /// <summary>Stands the decomposition down over output it produced but nothing can use.</summary>
    /// <remarks>
    ///     Through the ordinary retry policy, so the first answer to malformed output is another attempt carrying the
    ///     schema error in its objective — the cheapest correction loop, since what wrote the document can fix it —
    ///     and a human when that is spent. Two deliberate effects on a row that had SUCCEEDED: <c>EndedAtUtc</c> keeps
    ///     the success's stamp, because the agent really did finish then; and <c>OutputJson</c> is REPLACED by the
    ///     refusal, since the panel explains the row's current state and the promoted artifact still holds the output.
    /// </remarks>
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
            new DevWorkflowFailure
            {
                FailureClass = DevWorkflowFailureClasses.Configuration,
                SanitizedReason = reason,
                OutputJson = JsonSerializer.Serialize(new RejectedOutput
                    {
                        Status = DevWorkflowNodeOutputStatuses.Failed,
                        Attempt = producer.Attempt,
                        FailureClass = DevWorkflowFailureClasses.Configuration,
                        MaterializationError = reason
                    },
                    JsonOptions)
            },
            cancellationToken);

    /// <summary>
    ///     The newest task package this node produced, parsed — or the sentence an operator and the next attempt are
    ///     both told.
    /// </summary>
    /// <remarks>
    ///     Deliberately NOT attempt-scoped, and this is the one place that reading is right. Artifacts are run-scoped,
    ///     so a re-attempt that saved nothing leaves attempt 1's package the newest, and judging attempt 2 on it is
    ///     correct here because the package IS the node's output: an attempt producing no new one corrected nothing,
    ///     and the second refusal stands the node down. A node PANEL takes the opposite verdict for the same reason —
    ///     there the question is "what did this attempt do", here it is "is there a usable package on this run yet".
    /// </remarks>
    private async Task<TaskPackage> ReadPackageAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowMaterialization materialization,
        DevWorkflowNodeRunSnapshot producer,
        CancellationToken cancellationToken)
    {
        var artifacts = await store.ListArtifactsAsync(run.Id, sinceSequence: 0, cancellationToken);
        var artifact = artifacts.Where(entry => string.Equals(entry.ProducingNodeKey, producer.NodeKey, StringComparison.Ordinal)
                                                && entry.Kind == materialization.ArtifactKind
                                                && entry is { IsValid: true, IsLatest: true })
                                .MaxBy(static entry => entry.Sequence);
        if (artifact is null)
        {
            return TaskPackage.Rejected($"Node '{producer.NodeKey}' decomposes the work, but produced no {materialization.ArtifactKind} artifact to decompose it from.");
        }

        var read = await _blobs.ReadAsync(run.Id, artifact.Id, artifact.ContentSha256, artifact.SizeBytes, cancellationToken);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            return TaskPackage.Rejected($"The {materialization.ArtifactKind} artifact '{artifact.Name}' did not read back ({read.Status}), so there is nothing to decompose.");
        }

        return Parse(Encoding.UTF8.GetString(read.Content.Span), artifact.Id, artifact.Name);
    }

    /// <summary>
    ///     The fixed task-package schema: an array of
    ///     <c>{ id, title, goal, allowedPaths[], dependsOn[], acceptanceCriteria[] }</c>, at the root or under a
    ///     <c>tasks</c> property.
    /// </summary>
    /// <remarks>
    ///     A model writing an object around its list is the commonest shape of the same answer, and refusing it would
    ///     spend a whole re-attempt on punctuation.
    /// </remarks>
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

        // A JSON null in the array deserializes to a null ELEMENT despite the non-nullable annotation, and every reader below dereferences the entry. Refused at the parse boundary, the
        // one place that makes Tasks element-non-null: a hole throws out of the tick, and a tick that throws over an already-SUCCEEDED decomposition re-throws on every tick after it.
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is null)
            {
                return TaskPackage.Rejected($"The task package '{name}' has nothing at position {index + 1} where a task should be; every entry must be an object describing one task.");
            }
        }

        return new TaskPackage
        {
            Tasks = [.. items],
            ArtifactId = artifactId,
            Error = null
        };
    }

    /// <summary>
    ///     Every reason a well-formed package is still refused, in the order that names the smallest cause first.
    /// </summary>
    /// <remarks>
    ///     All of them are answered the same way — the node that wrote the package is asked to write it again with the
    ///     complaint in its objective — so what matters is that the sentence is specific enough for a model, and a
    ///     human reading the same field, to act on.
    /// </remarks>
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

        // Whether the template carries a DevTask ANYWHERE decides whether a task must name the files it changes: that node type's clone becomes a coder attempt, whose contract is to
        // export a patch. The whole subtree, not its root, because a template may root itself in an Agent briefing a DevTask below it. Read once: it cannot differ between tasks.
        var subtreeHasDevTask = graph.TemplateSubtreeHasDevTask(materialization);

        var ids = new HashSet<string>(StringComparer.Ordinal);

        // Every clone key this package WOULD take, against the task that first claimed it, built as the loop goes: the collision that wedges a run is as easily between two tasks of one
        // package as with an existing node, since the clone key is not injective, and the store's unique (run_id, node_key) answers with a refused insert that throws out of the tick.
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

            // For a template carrying a DevTask only. There a task becomes a coder attempt that cannot finish without exporting a NON-EMPTY patch, so a slice with nothing to change is
            // refused and re-refused until it blocks the run. 'changes' is the one signal that there IS something to change, so a package naming none is handed back while that is cheap.
            if (subtreeHasDevTask)
            {
                var changes = (task.Changes ?? []).Where(static change => !string.IsNullOrWhiteSpace(change)).ToList();
                if (changes.Count == 0)
                {
                    return $"Task '{task.Id}' names no file it will add or edit in 'changes', so there is nothing for a coder to implement. "
                           + "A task must change code; fold reading or surveying into the task that needs it.";
                }

                // The workspace confinement the coder's own tools enforce, asked here instead: an absolute path, one climbing out of the workspace, or one under protected Git state is a
                // file the coder would be refused for touching, so the decomposition is told now rather than three attempts later.
                if (changes.Find(static change => !DevelopmentWorkspaceSecurity.Confine(change, allowRoot: false).IsAccepted) is { } unusable)
                {
                    return $"Task '{task.Id}' names '{unusable}' in 'changes', which is not a file a coder could touch: "
                           + "every entry must be a path relative to the repository root, with no leading slash, no '..' above it and nothing under '.git'.";
                }
            }

            // Not enforced anywhere: the child brief carries title, requirements and acceptance criteria, and Dev Mode's workspace policy has no per-task path restriction to hand this
            // to, so a decomposition leaning on it for parallel-child isolation would get none silently. The field stays in the schema; this refuses a package that DEPENDS on it.
            if (task.AllowedPaths is { Count: > 0 })
            {
                return $"Task '{task.Id}' restricts itself to specific paths with 'allowedPaths', which this version does not enforce. "
                       + "Remove the field and describe the boundary in the goal instead.";
            }

            // EVERY node of the subtree, not just its root: a graph declaring a node named like one of the other clones collides just as hard, and the collision surfaces at the store as
            // a refused insert, which throws out of the tick and wedges the run rather than standing this node down.
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
    /// </summary>
    /// <remarks>
    ///     The rewrite works on the stored JSON rather than the parsed projection: the projection keeps only what the
    ///     runtime routes on, and re-serialising it would silently drop every authoring field the editor put there. So
    ///     each clone is the template node's own JSON with its key — and any retry target — rewritten.
    /// </remarks>
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

        // A leaf of the template is what the join waits for, computed from the template's OWN edges, so the join waits for every task's last node rather than firing while they run.
        // simplified: an `Any` join is the author's — one task through it leaves a single live inbound edge, which parse refuses, so the run fails as unroutable rather than hanging.
        var leaves = subtree.Where(key => !graph.OutboundEdges(key).Any(edge => subtree.Contains(edge.To))).ToList();
        var clones = new List<Clone>();
        var wired = new HashSet<(string From, string To)>();
        foreach (var (task, index) in tasks.Select(static (task, index) => (task, index + 1)))
        {
            var brief = JsonSerializer.Serialize(new DevTaskBrief
            {
                Title = Present(task.Title),
                Requirements = RequirementsFor(task),
                AcceptanceCriteriaJson = Criteria(task.AcceptanceCriteria)
            }, JsonOptions);
            foreach (var key in subtree.OrderBy(static key => key, StringComparer.Ordinal))
            {
                var clone = (JsonObject)templates[key].DeepClone();
                clone["nodeKey"] = CloneKey(key, task.Id!);
                if (clone["retryTarget"]?.GetValue<string>() is { } retryTarget && subtree.Contains(retryTarget))
                {
                    clone["retryTarget"] = CloneKey(retryTarget, task.Id!);
                }

                nodes.Add(clone);
                clones.Add(new Clone
                {
                    NodeKey = CloneKey(key, task.Id!),
                    Node = graph.Nodes[key],
                    InputJson = brief,
                    Index = index
                });

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

        // The decomposition's OWN edge into the join is KEPT. A join waiting on an already-Succeeded node does not fire early — All and Any both wait while any inbound edge is Pending —
        // and removing it takes the decomposition off every path back from the join, which upstream artifact resolution walks: the node behind it would lose the task package.
        return new Expansion
        {
            Clones = clones,
            GraphJson = root.ToJsonString(JsonOptions)
        };

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

    /// <summary>
    ///     A decomposition that legitimately answered "there is no follow-up work" (ruling D12).
    /// </summary>
    /// <remarks>
    ///     The graph is left as it is — the join keeps its edge from this node and fires on it — and what is written is
    ///     the commit marker plus ONE already-succeeded row per validation node in the template subtree. Without them
    ///     <c>GRAPH-C4-3</c> blocks a downstream apply, because an unmaterialized template key has no row and "nothing
    ///     to validate" would read as "nothing validated it". Such a row never makes a run look finished, because
    ///     <c>GRAPH-C4-1</c>'s fourth step keeps a template-subtree node non-terminal. A subtree with none writes a bare marker.
    /// </remarks>
    private static async Task<int> NothingToDoAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowMaterialization materialization,
        DevWorkflowNodeRunSnapshot producer,
        Guid artifactId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var checks = graph.TemplateSubtree(materialization)
                          .Select(key => graph.Nodes[key])
                          .Where(static node => node is { NodeType: DevWorkflowNodeType.Tool, ToolMode: DevWorkflowToolMode.Validate })
                          .OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                          .ToList();
        if (checks.Count == 0)
        {
            // The detail says so: this is the one graph.changed that changes no graph, and a consumer refetching on the token alone gets the same revision back. `graphRevision` is the
            // run's CURRENT one, which has not moved.
            _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand
                {
                    RunId = run.Id,
                    ExpectedVersion = DevWorkflowVersions.Any,
                    EventType = DevWorkflowEventTypes.GraphChanged,
                    NodeRunId = producer.Id,
                    OperationId = operationId,
                    DetailJson = JsonSerializer.Serialize(new ExpansionDetail
                        {
                            NodeKey = producer.NodeKey,
                            TaskCount = 0,
                            SourceArtifactId = artifactId,
                            GraphRevision = run.GraphRevision,
                            RevisionBumped = false
                        },
                        JsonOptions)
                },
                cancellationToken);
            return 1;
        }

        // Under the SAME operation id the marker would have taken, so the replay guard is unchanged and rows and marker commit together with no window in which one exists without the
        // other. A null GraphJson is what makes the marker node.materialized rather than graph.changed, the honest token here because no graph changed.
        _ = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand
            {
                RunId = run.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                OperationId = operationId,
                NodeRuns =
                [
                    .. checks.Select(check => new DevWorkflowNodeRunSeed
                    {
                        NodeRunId = Guid.NewGuid(),
                        NodeKey = check.NodeKey,
                        NodeType = check.NodeType,
                        MaxAttempts = check.MaxAttempts,
                        AgentDefinitionId = check.AgentDefinitionId,
                        DevelopmentProjectId = producer.DevelopmentProjectId,
                        InputJson = null,
                        PolicyResolutionJson = null,
                        MaterializedFromNodeRunId = producer.Id,
                        // Not clone n: this row stands for ZERO clones, which is a different fact and
                        // the one a reader of the panel needs.
                        MaterializationIndex = null,
                        Status = DevWorkflowNodeRunStatus.Succeeded,
                        OutputJson = JsonSerializer.Serialize(new NotApplicableOutput
                            {
                                Status = DevWorkflowNodeOutputStatuses.Succeeded,
                                Attempt = 1,
                                Verdict = DevWorkflowNodeOutputVerdicts.ValidationNotApplicable,
                                ProducedBy = producer.NodeKey,
                                TaskPackageArtifactId = artifactId
                            },
                            JsonOptions)
                    })
                ],
                GraphJson = null
            },
            cancellationToken);
        return checks.Count;
    }

    private static string CloneKey(string nodeKey, string taskId) =>
        $"{nodeKey}{CloneSeparator}{taskId}";

    /// <summary>
    ///     What the coder is told to implement: the task's goal, and the files the decomposition said it would touch.
    /// </summary>
    /// <remarks>
    ///     Folded into the requirements rather than carried as a field of its own, because <c>requirements</c> is the
    ///     whole of what the implementation lane renders to the coder: a second field would have to be threaded
    ///     through the brief, the executor's reader and the Development task to say what a sentence says here. It is
    ///     guidance, not a boundary — nothing refuses a coder for touching a file this does not name.
    /// </remarks>
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

    /// <summary>One task of the task package. Every member is optional at the parser; <see cref="Reject" /> says which are not.</summary>
    private sealed record TaskPackageItem(
        string? Id,
        string? Title,
        string? Goal,
        IReadOnlyList<string>? Changes,
        IReadOnlyList<string>? AllowedPaths,
        IReadOnlyList<string>? DependsOn,
        IReadOnlyList<string>? AcceptanceCriteria);

    /// <summary>A read package: its tasks and the artifact they came from, or the reason there are none.</summary>
    private sealed record TaskPackage
    {
        public required IReadOnlyList<TaskPackageItem> Tasks { get; init; }

        public required Guid ArtifactId { get; init; }

        public required string? Error { get; init; }

        public static TaskPackage Rejected(string error) =>
            new()
            {
                Tasks = [],
                ArtifactId = Guid.Empty,
                Error = error
            };
    }

    /// <summary>One node run to create: its new key, the template node it copies, and the brief its task carries.</summary>
    private sealed record Clone
    {
        public required string NodeKey { get; init; }

        public required DevWorkflowGraphNode Node { get; init; }

        public required string InputJson { get; init; }

        public required int Index { get; init; }
    }

    private sealed record Expansion
    {
        public required IReadOnlyList<Clone> Clones { get; init; }

        public required string GraphJson { get; init; }
    }

    /// <summary>
    ///     What a materialized child is told to implement — the seam the implementation lane reads, whose
    ///     <c>requirements</c> is mandatory there and so is written from the task's <c>goal</c> here.
    /// </summary>
    private sealed record DevTaskBrief
    {
        public required string? Title { get; init; }

        public required string Requirements { get; init; }

        public required string? AcceptanceCriteriaJson { get; init; }
    }

    /// <summary>What the commit marker carries when there is no expansion to describe: which node, off what, and that the graph did not move.</summary>
    private sealed record ExpansionDetail
    {
        public required string NodeKey { get; init; }

        public required int TaskCount { get; init; }

        public required Guid SourceArtifactId { get; init; }

        public required int GraphRevision { get; init; }

        public required bool RevisionBumped { get; init; }
    }

    /// <summary>What a not-applicable validation row says it produced.</summary>
    /// <remarks>
    ///     <c>status</c> is the routing vocabulary's own <c>succeeded</c>, so a conditional out-edge on the template's
    ///     validation node fires exactly as a real pass would; <c>verdict</c> keeps a reader from taking it for one.
    /// </remarks>
    private sealed record NotApplicableOutput
    {
        public required string Status { get; init; }

        public required int Attempt { get; init; }

        public required string Verdict { get; init; }

        public required string ProducedBy { get; init; }

        public required Guid TaskPackageArtifactId { get; init; }
    }

    /// <summary>A decomposition whose own output it cannot use. The reason travels into the next attempt's objective.</summary>
    private sealed record RejectedOutput
    {
        public required string Status { get; init; }

        public required int Attempt { get; init; }

        public required string FailureClass { get; init; }

        public required string MaterializationError { get; init; }
    }
}
