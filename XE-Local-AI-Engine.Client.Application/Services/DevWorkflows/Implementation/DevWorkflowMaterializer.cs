namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

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
            _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand(run.Id,
                                   DevWorkflowVersions.Any,
                                   DevWorkflowEventTypes.GraphChanged,
                                   producer.Id,
                                   operationId,
                                   DetailJson: JsonSerializer.Serialize(new ExpansionDetail(producer.NodeKey, TaskCount: 0, package.ArtifactId), JsonOptions)),
                               cancellationToken)
                           .ConfigureAwait(false);
            return 1;
        }

        var expansion = Compose(graph, run.GraphJson, node, materialization, tasks);
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
                                       PolicyResolutionJson: null,
                                       producer.Id,
                                       clone.Index))
                               ],
                               expansion.GraphJson),
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

        return items is null
            ? TaskPackage.Rejected($"The task package '{name}' must be an array of tasks, or an object with a 'tasks' array.")
            : new TaskPackage([.. items], artifactId, Error: null);
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

        if (existingNodeRuns + (tasks.Count * graph.TemplateSubtree(materialization).Count) > maxNodeRunsPerRun)
        {
            return $"Expanding {tasks.Count} tasks would take this run past the {maxNodeRunsPerRun} node runs it may carry.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
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

            if (graph.Nodes.ContainsKey(CloneKey(materialization.TemplateNodeKey, task.Id)))
            {
                return $"Task '{task.Id}' would take the node key of one this run already carries.";
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
            var brief = JsonSerializer.Serialize(new DevTaskBrief(Present(task.Title), task.Goal!, Criteria(task.AcceptanceCriteria)), JsonOptions);
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

        // The join now waits on the children instead of on the node that decided what they are. Left in place, it would
        // fire the moment the decomposition succeeded and let the run complete over work that had not started.
        foreach (var edge in edges.OfType<JsonObject>()
                                  .Where(edge => edge["from"]?.GetValue<string>() == node.NodeKey && edge["to"]?.GetValue<string>() == materialization.JoinNodeKey)
                                  .ToList())
        {
            _ = edges.Remove(edge);
        }

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

    private static string? Present(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The task's acceptance criteria as the JSON array Dev Mode stores, or nothing when it named none.</summary>
    private static string? Criteria(IReadOnlyList<string>? acceptanceCriteria) =>
        acceptanceCriteria is { Count: > 0 } criteria ? JsonSerializer.Serialize(criteria, JsonOptions) : null;

    /// <summary>One task of the §5.9 package. Every member is optional at the parser; <see cref="Reject" /> says which are not.</summary>
    private sealed record TaskPackageItem(string? Id,
        string? Title,
        string? Goal,
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

    /// <summary>What the commit marker carries when there is no expansion to describe: which node, and off what.</summary>
    private sealed record ExpansionDetail(string NodeKey, int TaskCount, Guid SourceArtifactId);

    /// <summary>A decomposition whose own output it cannot use. The reason travels into the next attempt's objective.</summary>
    private sealed record RejectedOutput(string Status, int Attempt, string FailureClass, string MaterializationError);
}
