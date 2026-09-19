namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>One saved Open Canvas workflow, decrypted, waiting for the Graph Workflow tables to exist.</summary>
public sealed class CanvasWorkflowImportCandidate
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string GraphJson { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     Everything the pre-migration read found. <see cref="FailedCount" /> is the rows that could not be decrypted or
///     did not parse as JSON at all: they are unrecoverable once the drop migration commits, so they are counted and
///     named in the log rather than passed on.
/// </summary>
public sealed class CanvasWorkflowImportSnapshot
{
    public required IReadOnlyList<CanvasWorkflowImportCandidate> Candidates { get; init; }

    public required int FailedCount { get; init; }
}

/// <summary>
///     The one-shot conversion of saved Open Canvas (Preview) workflows into Graph Workflow definitions, run once at
///     startup on the first start of the build that removes Open Canvas.
///     <para>
///         It runs REGARDLESS of <c>GraphWorkflows:Enabled</c>. An operator who never turns the feature on must not
///         silently lose their canvases, so the flag gates the request path and this import is outside it.
///     </para>
///     <para>
///         Split in two because an EF migration cannot decrypt <c>graph_json</c> — it has no node key — while the
///         <c>DropCanvasWorkflows</c> migration removes the source table before any hosted service starts.
///         <see cref="ReadAsync" /> therefore runs BEFORE migrations and <see cref="ImportAsync" /> after them. The
///         idempotency mechanism is the source table itself: on every later start it is gone, the read is a no-op, and
///         there is no marker table or flag column to keep honest.
///     </para>
///     <para>
///         Nothing here may depend on a Preview type: the read parses the stored blob into private records of its own
///         so the Preview namespace can be deleted out from under it in the same slice.
///     </para>
///     <para>
///         It also runs under <c>--reset-admin-password</c>: that branch of <c>Program</c> returns only AFTER the
///         migration pass, so the read, the migrations and this write all happen first. The knowledge-downgrade
///         commands are the one launch path that returns before migrations and therefore before the import.
///     </para>
/// </summary>
public static class CanvasWorkflowImport
{
    /// <summary>The prefix an operator greps for: a definition carrying it will not run until it is edited.</summary>
    private const string NeedsAttentionPrefix = "IMPORT NEEDS ATTENTION: ";

    /// <summary><c>graph_workflow_definitions.name</c> and <c>.description</c> column bounds.</summary>
    private const int MaxNameLength = 255;

    private const int MaxDescriptionLength = 1024;

    /// <summary>The node and edge key charset the Graph Workflow parser holds every key to.</summary>
    private const int MaxKeyLength = 64;

    /// <summary>Read with the Web defaults the canvas blob was written under: camelCase members, case-insensitive.</summary>
    private static readonly JsonSerializerOptions CanvasSerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Every saved canvas, decrypted, read BEFORE migrations while <c>canvas_workflows</c> still exists.
    ///     <para>
    ///         Raw SQL with EVERY column aliased: an unmapped-type query binds result columns to property names, so
    ///         <c>graph_json</c> would never reach <c>GraphJson</c> and the read would throw with the drop migration
    ///         still committing behind it. No <c>LIMIT</c> and no option — a cap plus an unconditional drop destroys
    ///         everything past the cap as its normal outcome.
    ///     </para>
    /// </summary>
    public static async Task<CanvasWorkflowImportSnapshot> ReadAsync(NodeChatDbContext dbContext, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);

        // The table is gone on every start after the first, which is what makes this import one-shot.
        var tables = await dbContext.Database
                                    .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' AND name='canvas_workflows'")
                                    .ToListAsync(cancellationToken);
        if (tables.Count == 0)
        {
            return new CanvasWorkflowImportSnapshot { Candidates = [], FailedCount = 0 };
        }

        var rows = await dbContext.Database
                                  .SqlQueryRaw<CanvasWorkflowRow>("SELECT id AS Id, name AS Name, graph_json AS GraphJson, created_at_utc AS CreatedAtUtc "
                                                                  + "FROM canvas_workflows ORDER BY created_at_utc ASC, id ASC")
                                  .ToListAsync(cancellationToken);

        logger.LogInformation("Open Canvas one-shot import: {CanvasWorkflowCount} saved workflow(s) read before migrations.", rows.Count);

        var candidates = new List<CanvasWorkflowImportCandidate>(rows.Count);
        var failed = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var plaintext = Encoding.UTF8.GetString(NodePayloadProtector.Decrypt(row.GraphJson,
                    dbContext.NodeEncryptionKey.Span,
                    Guid.Empty,
                    row.Id,
                    "graph_json"));

                // The only skip cause there is. A blob written by the Open Canvas endpoint decrypts under its own AAD
                // and parses, so a failure here means the row was already damaged before this slice touched it.
                JsonDocument.Parse(plaintext).Dispose();

                candidates.Add(new CanvasWorkflowImportCandidate { Id = row.Id, Name = row.Name ?? string.Empty, GraphJson = plaintext, CreatedAtUtc = row.CreatedAtUtc });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Broad on purpose: one damaged row must never cost the operator the rest of their canvases, and the
                // drop migration commits whether this read succeeded or not. The type, never the content.
                failed++;
                logger.LogError("Open Canvas workflow {CanvasWorkflowId} could not be read and will be lost when canvas_workflows is dropped. [{ErrorType}]",
                    row.Id,
                    exception.GetType().Name);
            }
        }

        return new CanvasWorkflowImportSnapshot { Candidates = candidates, FailedCount = failed };
    }

    /// <summary>
    ///     Writes every candidate, AFTER migrations have created the Graph Workflow tables and dropped
    ///     <c>canvas_workflows</c>.
    ///     <para>
    ///         The happy path goes through <see cref="IGraphWorkflowDefinitionService" />, which owns the parse, the
    ///         node cap and the hash the save endpoint uses. A graph it refuses is saved ANYWAY through
    ///         <see cref="IGraphWorkflowStore" /> with an <c>IMPORT NEEDS ATTENTION:</c> description: preserving the
    ///         row beats enforcing validity on data that is about to be deleted either way. Such a definition cannot
    ///         run until an operator edits it.
    ///     </para>
    /// </summary>
    public static async Task ImportAsync(IGraphWorkflowDefinitionService definitions,
        IGraphWorkflowStore store,
        CanvasWorkflowImportSnapshot snapshot,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(logger);

        if (snapshot.Candidates.Count == 0 && snapshot.FailedCount == 0)
        {
            return;
        }

        var imported = 0;
        var attention = 0;
        var failed = snapshot.FailedCount;
        foreach (var candidate in snapshot.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await ImportOneAsync(definitions, store, candidate, logger, cancellationToken);
            switch (outcome)
            {
                case ImportOutcome.Imported:
                    imported++;
                    break;
                case ImportOutcome.NeedsAttention:
                    attention++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        // Warning, not Information: this is an irreversible one-shot the operator had no chance to decline, and
        // Information is where it would be invisible.
        logger.LogWarning("Open Canvas one-shot import complete: {Imported} imported, {NeedsAttention} need attention, {Failed} failed. "
                          + "Open Canvas has been removed; canvas_workflows is dropped.",
            imported,
            attention,
            failed);
    }

    /// <summary>
    ///     One canvas graph as a Graph Workflow document. PURE and TOTAL: it always answers a document, never throws
    ///     and never refuses a graph. <c>Reasons</c> carries whatever it could not translate faithfully — the graph
    ///     travels either way, and the validator downstream decides whether the definition can run.
    ///     <para>
    ///         The wiring is NOT one-for-one. A Debug node is elided and the edge rewired around it, and every Pause
    ///         gains a context edge past it, because neither node forwarded its predecessor's document the way its
    ///         Graph Workflow counterpart does. Both are faithfulness, not liberties: see
    ///         <see cref="AddPauseContextEdges" /> and <see cref="ResolveTargets" />.
    ///     </para>
    /// </summary>
    internal static ImportMapResult MapGraph(string graphJson)
    {
        var reasons = new List<string>();
        var canvas = ReadCanvas(graphJson, reasons);

        // A JSON null sitting among the node or edge members deserializes to a null ELEMENT. It is valid JSON, so
        // ReadCanvas never sees an exception, and every walk below would dereference it. Dropped so the mapper stays
        // total.
        var nodes = (canvas.Nodes ?? []).OfType<CanvasNode>().ToList();
        var edges = (canvas.Edges ?? []).OfType<CanvasEdge>().ToList();
        if (nodes.Count != (canvas.Nodes?.Count ?? 0) || edges.Count != (canvas.Edges?.Count ?? 0))
        {
            reasons.Add("The stored canvas graph carried empty node or edge entries, which could name nothing and were dropped.");
        }

        // Debug nodes forward their input unchanged (they were a side-event tap), so eliding one and rewiring
        // X -> Debug -> Y into X -> Y preserves the run's meaning exactly.
        // Non-empty ids only: an id nothing can name is no use for rewiring, and treating "" as elided would swallow
        // every other node that also declares none.
        var elided = new HashSet<string>(nodes.Where(static node => IsKind(node.Kind, "Debug"))
                                              .Select(static node => node.Id)
                                              .Where(static id => !string.IsNullOrEmpty(id))!,
            StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var keyByCanvasId = new Dictionary<string, string>(StringComparer.Ordinal);
        var mappedNodes = new JsonArray();
        var nodeByKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var pauseKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (IsKind(node.Kind, "Debug"))
            {
                continue;
            }

            var canvasId = node.Id ?? string.Empty;
            var key = MintKey(canvasId, $"n{index}", used);
            if (!keyByCanvasId.TryAdd(canvasId, key))
            {
                reasons.Add($"Two canvas nodes share one id; node '{key}' kept its own key and every edge naming that id "
                            + $"was wired to '{keyByCanvasId[canvasId]}'.");
            }

            if (IsKind(node.Kind, "Pause"))
            {
                _ = pauseKeys.Add(key);
            }

            if (!string.IsNullOrWhiteSpace(node.ModelProfile))
            {
                // The Agent config has no profile member: it was a provider hint the new executor does not consult.
                // The reason names the node, never the value.
                reasons.Add($"The Open Canvas model profile on node '{key}' has no equivalent here and was dropped.");
            }

            var mappedNode = MapNode(node, key, canvas.StartText, reasons);
            mappedNodes.Add(mappedNode);

            // The context-edge guards read a successor's kind and join policy off the node this mapper actually
            // emitted, never off the canvas kind: the two guards below are stated over the DOCUMENT the validator
            // will read, so they cannot disagree with it.
            nodeByKey[key] = mappedNode;
        }

        var mappedEdges = MapEdges(edges, elided, keyByCanvasId, pauseKeys, nodeByKey, used, reasons);
        return new ImportMapResult
        {
            Document = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["nodes"] = mappedNodes,
                ["edges"] = mappedEdges
            },
            // Deduplicated: one dropped-edge complaint says as much as twenty, and this text reaches a 1024-character
            // description column.
            Reasons = [.. reasons.Distinct(StringComparer.Ordinal)]
        };
    }

    /// <summary>
    ///     One canvas, through the service first and the store second. A refusal is not a loss: the same document is
    ///     stored unvalidated with the refusal in its description.
    /// </summary>
    private static async Task<ImportOutcome> ImportOneAsync(IGraphWorkflowDefinitionService definitions,
        IGraphWorkflowStore store,
        CanvasWorkflowImportCandidate candidate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ImportMapResult map;
        string graphJson;
        string name;
        string provenance;
        try
        {
            // Inside the guard, not before it: this method is the only thing standing between a row that throws while
            // being mapped and every LATER canvas plus the summary line, and the source table is already dropped.
            map = MapGraph(candidate.GraphJson);
            graphJson = map.Document.ToJsonString();
            name = DefinitionName(candidate.Name);
            provenance = $"Imported from Open Canvas (canvas workflow {candidate.Id}).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Open Canvas workflow {CanvasWorkflowId} could not be translated and is lost. [{ErrorType}]",
                candidate.Id,
                exception.GetType().Name);
            return ImportOutcome.Failed;
        }

        try
        {
            var created = await definitions.CreateAsync(name, provenance, graphJson, cancellationToken);
            logger.LogInformation("Open Canvas workflow {CanvasWorkflowId} -> graph workflow definition {DefinitionId}, {NodeCount} nodes.",
                candidate.Id,
                created.Id,
                created.NodeCount);

            foreach (var reason in map.Reasons)
            {
                logger.LogWarning("Open Canvas workflow {CanvasWorkflowId} imported with a change: {Reason}", candidate.Id, reason);
            }

            return ImportOutcome.Imported;
        }
        catch (GraphWorkflowValidationException refusal)
        {
            return await SaveUnvalidatedAsync(store, candidate, map, graphJson, name, provenance, refusal, logger, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One canvas that cannot be written never stops the others.
            logger.LogError("Open Canvas workflow {CanvasWorkflowId} could not be imported. [{ErrorType}]", candidate.Id, exception.GetType().Name);
            return ImportOutcome.Failed;
        }
    }

    /// <summary>
    ///     The deliberate fallback: bypass <c>Validate</c> and store the graph as it is, tagged so the definition list
    ///     says out loud that it will not run. The store computes the hash and the schema version exactly as it does
    ///     for a clean save; the node count is what the mapper wrote, because there is no parse to take it from.
    /// </summary>
    private static async Task<ImportOutcome> SaveUnvalidatedAsync(IGraphWorkflowStore store,
        CanvasWorkflowImportCandidate candidate,
        ImportMapResult map,
        string graphJson,
        string name,
        string provenance,
        GraphWorkflowValidationException refusal,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var reason = string.Join(" ", map.Reasons.Append(refusal.Message));
        var description = NeedsAttentionDescription(reason, provenance);
        var nodeCount = map.Document["nodes"] is JsonArray nodes ? nodes.Count : 0;

        try
        {
            var created = await store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand { DefinitionId = Guid.NewGuid(), Name = name, GraphJson = graphJson, NodeCount = nodeCount, Description = description },
                                         cancellationToken);

            logger.LogWarning("Open Canvas workflow {CanvasWorkflowId} ('{CanvasWorkflowName}') was imported as graph workflow definition {DefinitionId} "
                              + "but will not run until it is edited: {Reason}",
                candidate.Id,
                name,
                created.Id,
                reason);
            return ImportOutcome.NeedsAttention;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Open Canvas workflow {CanvasWorkflowId} ('{CanvasWorkflowName}') could not be stored and is lost. [{ErrorType}]",
                candidate.Id,
                name,
                exception.GetType().Name);
            return ImportOutcome.Failed;
        }
    }

    /// <summary>
    ///     The stored blob, into records of this file's own. <c>kind</c> is read as a STRING — the canvas enum carried
    ///     a string converter — so nothing here needs the Preview enum to exist.
    /// </summary>
    private static CanvasGraph ReadCanvas(string graphJson, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
        {
            reasons.Add("The stored canvas graph was empty, so nothing could be translated from it.");
            return new CanvasGraph(null, null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<CanvasGraph>(graphJson, CanvasSerializerOptions) ?? new CanvasGraph(null, null, null);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            reasons.Add("The stored canvas graph did not read as a canvas graph, so nothing could be translated from it.");
            return new CanvasGraph(null, null, null);
        }
    }

    private static JsonObject MapNode(CanvasNode node, string key, string? startText, List<string> reasons)
    {
        if (IsKind(node.Kind, "Start"))
        {
            return new JsonObject
            {
                ["key"] = key,
                ["kind"] = "Start",
                ["label"] = "Start",
                ["config"] = new JsonObject
                {
                    ["inputSchema"] = null,
                    // A run input is a JSON document, and the editor renders a stored default as JSON text — a bare
                    // string would round-trip as unquoted prose the editor cannot parse. The seed text rides under one
                    // member, so the first Agent still sees it verbatim inside the Start node's input document.
                    ["defaultInput"] = string.IsNullOrEmpty(startText)
                        ? null
                        : new JsonObject
                        {
                            ["text"] = startText
                        }
                }
            };
        }

        if (IsKind(node.Kind, "Agent"))
        {
            return new JsonObject
            {
                ["key"] = key,
                ["kind"] = "Agent",
                ["label"] = node.Label ?? "Agent",

                // One try, not the three a hand-authored Agent node defaults to: an import is conservative, because
                // nobody watched the canvas run under this runtime.
                ["maxAttempts"] = 1,
                ["timeoutSeconds"] = null,
                ["config"] = new JsonObject
                {
                    ["agentDefinitionId"] = null,
                    ["instructions"] = node.Instructions ?? string.Empty,
                    ["model"] = node.Model,
                    ["reasoningEffort"] = node.ReasoningEffort,
                    ["responseJsonSchema"] = null,
                    ["includeUpstreamOutputs"] = true
                }
            };
        }

        if (IsKind(node.Kind, "Pause"))
        {
            return new JsonObject
            {
                ["key"] = key,
                ["kind"] = "Pause",
                ["label"] = node.Label ?? "Pause",
                ["config"] = new JsonObject
                {
                    ["prompt"] = "Approve and continue?",

                    // The canvas's Continue was a resume, not a decision, so Approve alone is the faithful
                    // translation — and one allowed decision with one matching out-edge satisfies the pre-flight rule.
                    ["allowedDecisions"] = new JsonArray("Approve"),
                    ["requireComment"] = false
                }
            };
        }

        if (IsKind(node.Kind, "End"))
        {
            return new JsonObject
            {
                ["key"] = key,
                ["kind"] = "End",
                ["label"] = "Done",
                ["config"] = new JsonObject
                {
                    ["outcome"] = "completed",
                    ["resultPath"] = null
                }
            };
        }

        // Written through with the kind as it stands rather than guessed at or dropped: the parser refuses it, the
        // definition lands as IMPORT NEEDS ATTENTION, and the operator sees what the canvas actually held.
        reasons.Add($"Node '{key}' has a canvas kind this runtime does not offer, so the definition will not run until it is changed.");
        return new JsonObject
        {
            ["key"] = key,
            ["kind"] = node.Kind,
            ["label"] = node.Label ?? key
        };
    }

    /// <summary>
    ///     Edges, with every elided Debug node walked through. An edge key is minted (<c>e{index}</c>) into the SAME
    ///     namespace the node keys took, because the parser holds both to one.
    /// </summary>
    private static JsonArray MapEdges(IReadOnlyList<CanvasEdge> edges,
        HashSet<string> elided,
        Dictionary<string, string> keyByCanvasId,
        HashSet<string> pauseKeys,
        IReadOnlyDictionary<string, JsonObject> nodeByKey,
        HashSet<string> used,
        List<string> reasons)
    {
        var successors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!successors.TryGetValue(edge.SourceId ?? string.Empty, out var targets))
            {
                targets = [];
                successors[edge.SourceId ?? string.Empty] = targets;
            }

            targets.Add(edge.TargetId ?? string.Empty);
        }

        var mapped = new JsonArray();
        var pairs = new HashSet<(string From, string To)>();
        var strandedDebug = new HashSet<string>(StringComparer.Ordinal);
        var circularDebug = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var edge in edges)
        {
            var sourceId = edge.SourceId ?? string.Empty;
            if (elided.Contains(sourceId))
            {
                // Its inbound edges carry it: X -> Debug -> Y is emitted when X's own edge is walked.
                continue;
            }

            if (!keyByCanvasId.TryGetValue(sourceId, out var from))
            {
                reasons.Add("An edge left a node the canvas does not declare and was dropped.");
                continue;
            }

            foreach (var targetId in ResolveTargets(edge.TargetId ?? string.Empty, elided, successors, strandedDebug, circularDebug))
            {
                if (!keyByCanvasId.TryGetValue(targetId, out var to))
                {
                    reasons.Add($"An edge out of node '{from}' named a node the canvas does not declare and was dropped.");
                    continue;
                }

                if (!pairs.Add((from, to)))
                {
                    continue;
                }

                var mappedEdge = new JsonObject
                {
                    ["key"] = MintKey($"e{index}", $"e{index}", used),
                    ["from"] = from,
                    ["to"] = to
                };

                if (pauseKeys.Contains(from))
                {
                    // The one answer the pause offers has to have somewhere to go, or the pre-flight rule refuses it.
                    mappedEdge["label"] = "approved";
                    mappedEdge["condition"] = new JsonObject
                    {
                        ["path"] = "output.decision",
                        ["op"] = "Eq",
                        ["value"] = "Approve"
                    };
                }

                mapped.Add(mappedEdge);
                index++;
            }
        }

        AddPauseContextEdges(pauseKeys, nodeByKey, pairs, used, mapped, index);

        foreach (var stranded in strandedDebug.Order(StringComparer.Ordinal))
        {
            reasons.Add($"A canvas Debug node ('{Sanitize(stranded)}') had nothing after it, so the path into it now ends nowhere.");
        }

        foreach (var circular in circularDebug.Order(StringComparer.Ordinal))
        {
            reasons.Add($"A canvas Debug node ('{Sanitize(circular)}') led only back into itself, so the path into it now ends nowhere.");
        }

        return mapped;
    }

    /// <summary>
    ///     The context edge around an imported Pause: one unconditional edge from the pause's nearest NON-Pause
    ///     ancestor to a successor that would otherwise read nothing but the approval.
    ///     <para>
    ///         Open Canvas's Pause was a pass-through resume — its post-adapter forwarded the answer it was waiting on
    ///         unchanged — while a Graph Workflow Pause writes a decision document of its own
    ///         (<c>{decision, comment, payload}</c>, <c>GraphWorkflowDocuments.PauseOutput</c>). A node's <c>input</c>
    ///         is its ONE satisfied predecessor's output document and becomes the <c>upstream</c> map only when there
    ///         are several (<c>GraphWorkflowDocuments.ComposeInput</c>, fed by
    ///         <c>GraphWorkflowInlineExecutor.Upstream</c>). So <c>A -> Pause -> B</c> mapped one-for-one hands B the
    ///         approval metadata and never A's answer, and a Pause before an End loses the result the same way.
    ///     </para>
    ///     <para>
    ///         The successor keeps the default <c>All</c> join policy, so it is admitted only once BOTH the content
    ///         edge and the pause's own <c>approved</c> edge are satisfied — never ahead of the approval — and with two
    ///         satisfied predecessors its <c>input</c> is the <c>upstream</c> map <c>{ "&lt;X&gt;": …, "&lt;P&gt;": … }</c>.
    ///         An Agent carries <c>includeUpstreamOutputs: true</c> and so sees the ancestor's text; an End maps with
    ///         <c>resultPath: null</c> and so carries both documents. A node may hold two inbound edges — the graph
    ///         indexes them per node and fan-in is the node's own join policy — and the only rule over one pair is that
    ///         at most one edge may be unconditional, which the <paramref name="pairs" /> guard below keeps.
    ///     </para>
    ///     <para>
    ///         Keyed on the SUCCESSOR and gated by the same three guards as the editor's advice
    ///         (<c>GraphWorkflowGraph.PauseContextWarnings</c>), so the importer cannot advise an edge the validator
    ///         would refuse or that would change when a node runs. A successor qualifies only when it is STARVED —
    ///         every one of its inbound edges leaves a Pause, so there is no other route for the content — when its
    ///         <c>joinPolicy</c> is not <c>Any</c> (read off the mapped node, never off its kind: an unconditional
    ///         content edge would admit an <c>Any</c> node on its own, ahead of every approval), and when the nearest
    ///         non-Pause ancestor is UNIQUE and is not a <c>Condition</c>. Two candidates means mutually exclusive
    ///         branches, and edges from both would hang an <c>All</c> successor on the branch never taken; a Condition
    ///         would receive a second unconditional out-edge, which <c>GraphWorkflowGraph.ValidateCondition</c>
    ///         refuses.
    ///     </para>
    ///     <para>
    ///         The ancestry walk still passes through consecutive pauses, so <c>A -> P1 -> P2 -> B</c> gains both
    ///         <c>A -> P2</c> and <c>A -> B</c>: the second pause sees the content it is approving too.
    ///     </para>
    ///     <para>
    ///         Every guard but the first is unreachable for a real import — an Open Canvas graph carries no Condition,
    ///         no Join and no join policy — and they are stated anyway because the rule, not today's vocabulary, is
    ///         what the next node kind has to keep holding.
    ///     </para>
    /// </summary>
    private static void AddPauseContextEdges(HashSet<string> pauseKeys,
        IReadOnlyDictionary<string, JsonObject> nodeByKey,
        HashSet<(string From, string To)> pairs,
        HashSet<string> used,
        JsonArray mapped,
        int index)
    {
        // A snapshot: the walk reads the canvas's own wiring and never the context edges this loop adds to it.
        var wiring = pairs.ToList();

        // Reached through the pauses, but DECIDED on the successor: a pause with no successor is in no pair at all and
        // so gets nothing to bypass to. That graph is already an IMPORT NEEDS ATTENTION case — the pre-flight rule
        // refuses a pause whose one answer arrives nowhere — and inventing an edge here would not save it. A successor
        // several pauses reach is considered ONCE, over the union of what all of them are fed by.
        var advised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var successor in pauseKeys.Order(StringComparer.Ordinal)
                                           .SelectMany(pause => wiring.Where(pair => string.Equals(pair.From, pause, StringComparison.Ordinal))
                                                                      .Select(static pair => pair.To)
                                                                      .Order(StringComparer.Ordinal)))
        {
            if (!advised.Add(successor))
            {
                continue;
            }

            var inbound = wiring.Where(pair => string.Equals(pair.To, successor, StringComparison.Ordinal))
                                .Select(static pair => pair.From)
                                .Distinct(StringComparer.Ordinal)
                                .ToList();

            // Guard 1 — STARVED. An inbound edge that does not leave a Pause already carries the content, so the
            // successor loses nothing and a second unconditional edge would only change when it is admitted.
            if (!inbound.TrueForAll(pauseKeys.Contains))
            {
                continue;
            }

            // Guard 2 — an 'Any' successor fires on its FIRST satisfied edge, so an unconditional content edge would
            // admit it ahead of every approval, including when all of them are rejected. Read off joinPolicy alone.
            if (JoinPolicyOf(nodeByKey, successor) == GraphWorkflowJoinPolicy.Any)
            {
                continue;
            }

            // Guard 3 — the ancestor has to be UNIQUE and not a Condition, or the advised edge hangs an 'All'
            // successor on a branch never taken, or lands a second unconditional out-edge on a Condition.
            var ancestors = inbound.SelectMany(pause => NonPauseAncestors(pause, pauseKeys, wiring))
                                   .Distinct(StringComparer.Ordinal)
                                   .Order(StringComparer.Ordinal)
                                   .ToList();
            if (ancestors is not [var ancestor] || KindOf(nodeByKey, ancestor) == GraphWorkflowNodeKind.Condition)
            {
                continue;
            }

            // A pair the canvas already wires needs no second edge — and a second UNCONDITIONAL one over the same
            // pair is a validation error. A self-loop would be a cycle.
            if (string.Equals(ancestor, successor, StringComparison.Ordinal) || !pairs.Add((ancestor, successor)))
            {
                continue;
            }

            mapped.Add(new JsonObject
            {
                ["key"] = MintKey($"e{index}", $"e{index}", used),
                ["from"] = ancestor,
                ["to"] = successor,
                ["label"] = "context"
            });
            index++;
        }
    }

    /// <summary>
    ///     A mapped node's join policy, parsed with the SAME token reader the graph parser uses so the importer cannot
    ///     read a member the validator would read differently. Absent — which is every node this mapper emits today —
    ///     or unparseable falls back to the parser's own default.
    /// </summary>
    private static GraphWorkflowJoinPolicy JoinPolicyOf(IReadOnlyDictionary<string, JsonObject> nodeByKey, string key) =>
        nodeByKey.TryGetValue(key, out var node)
        && GraphWorkflowTokens.TryParseName<GraphWorkflowJoinPolicy>(node["joinPolicy"]?.GetValue<string>(), out var parsed)
            ? parsed
            : GraphWorkflowJoinPolicy.All;

    /// <summary>A mapped node's kind, read the same way; an unknown key answers a kind no guard matches.</summary>
    private static GraphWorkflowNodeKind? KindOf(IReadOnlyDictionary<string, JsonObject> nodeByKey, string key) =>
        nodeByKey.TryGetValue(key, out var node)
        && GraphWorkflowTokens.TryParseName<GraphWorkflowNodeKind>(node["kind"]?.GetValue<string>(), out var parsed)
            ? parsed
            : null;

    /// <summary>
    ///     The nodes a pause's content really comes from: its predecessors, with consecutive Pause nodes walked
    ///     through, because a pause's own output is the approval rather than the answer. The seen-set stops the walk on
    ///     a damaged row that loops a pause back into itself. <c>Start</c> is a fine answer — its output is the run's
    ///     input, which is exactly the content a node behind the pause would otherwise have read.
    /// </summary>
    private static List<string> NonPauseAncestors(string pause, HashSet<string> pauseKeys, List<(string From, string To)> wiring)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            pause
        };
        var pending = new Stack<string>();
        pending.Push(pause);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var predecessor in wiring.Where(pair => string.Equals(pair.To, current, StringComparison.Ordinal)).Select(static pair => pair.From))
            {
                if (!seen.Add(predecessor))
                {
                    continue;
                }

                if (pauseKeys.Contains(predecessor))
                {
                    pending.Push(predecessor);
                }
                else
                {
                    resolved.Add(predecessor);
                }
            }
        }

        return [.. resolved.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Where an edge into <paramref name="targetId" /> really lands: itself, or — when it is an elided Debug node —
    ///     whatever that node pointed at, transitively. A Debug node with no successor is recorded in
    ///     <paramref name="stranded" /> rather than silently swallowing the edge; one whose successors only lead back
    ///     to Debug nodes the walk already passed through resolves to nothing for the same reason and is recorded in
    ///     <paramref name="circular" />, because the seen-set stops the walk without either list ever growing.
    /// </summary>
    private static List<string> ResolveTargets(string targetId,
        HashSet<string> elided,
        Dictionary<string, List<string>> successors,
        HashSet<string> stranded,
        HashSet<string> circular)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        var recordedStranded = false;
        pending.Push(targetId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (!elided.Contains(current))
            {
                resolved.Add(current);
                continue;
            }

            if (!successors.TryGetValue(current, out var next) || next.Count == 0)
            {
                _ = stranded.Add(current);
                recordedStranded = true;
                continue;
            }

            foreach (var successor in next)
            {
                pending.Push(successor);
            }
        }

        if (resolved.Count == 0 && !recordedStranded)
        {
            _ = circular.Add(targetId);
        }

        return resolved;
    }

    /// <summary>
    ///     A key inside the parser's charset and unique across the ONE node-and-edge namespace. The canvas id is kept
    ///     whenever it is already legal, so an imported graph reads like the canvas it came from.
    /// </summary>
    private static string MintKey(string preferred, string fallback, HashSet<string> used)
    {
        var sanitized = Sanitize(preferred);
        if (sanitized.Length > 0 && used.Add(sanitized))
        {
            return sanitized;
        }

        var candidate = Sanitize(fallback);
        if (candidate.Length == 0)
        {
            candidate = "n";
        }

        var suffix = 2;
        var minted = candidate;
        while (!used.Add(minted))
        {
            minted = $"{candidate}_{suffix++}";
        }

        return minted;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxKeyLength));
        foreach (var character in value)
        {
            if (builder.Length == MaxKeyLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            {
                _ = builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsKind(string? kind, string expected) =>
        string.Equals(kind, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>A canvas name is a human label with no length rule of its own; the definition column has one.</summary>
    private static string DefinitionName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Imported Open Canvas workflow" : Truncate(name, MaxNameLength);

    /// <summary>
    ///     The prefix and the provenance are what an operator reads; the reason is what gets trimmed if the column
    ///     cannot hold all three.
    /// </summary>
    private static string NeedsAttentionDescription(string reason, string provenance)
    {
        var room = MaxDescriptionLength - NeedsAttentionPrefix.Length - provenance.Length - 2;
        return room <= 0
            ? Truncate(NeedsAttentionPrefix + provenance, MaxDescriptionLength)
            : $"{NeedsAttentionPrefix}{Truncate(reason, room)} {provenance}";
    }

    /// <summary>Cuts to a length the column accepts without leaving half a surrogate pair behind.</summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value[..cut];
    }

    private enum ImportOutcome
    {
        Imported,
        NeedsAttention,
        Failed
    }

    /// <summary>The raw row, with every column aliased to the property that binds it.</summary>
    private sealed record CanvasWorkflowRow(Guid Id, string? Name, byte[] GraphJson, long CreatedAtUtc);

    private sealed record CanvasGraph(string? StartText, IReadOnlyList<CanvasNode?>? Nodes, IReadOnlyList<CanvasEdge?>? Edges);

    private sealed record CanvasNode(string? Id, string? Kind, string? Label, string? Instructions, string? Model, string? ModelProfile, string? ReasoningEffort);

    private sealed record CanvasEdge(string? SourceId, string? TargetId);
}

/// <summary>
///     What the mapper made of one canvas graph. <see cref="Reasons" /> is what it could not translate faithfully —
///     empty for a graph that came across whole.
/// </summary>
internal sealed class ImportMapResult
{
    public required JsonNode Document { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}
