namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>One saved Open Canvas workflow, decrypted, waiting for the Graph Workflow tables to exist.</summary>
public sealed record CanvasWorkflowImportCandidate(Guid Id, string Name, string GraphJson, long CreatedAtUtc);

/// <summary>
///     Everything the pre-migration read found. <see cref="FailedCount" /> is the rows that could not be decrypted or
///     did not parse as JSON at all: they are unrecoverable once the drop migration commits, so they are counted and
///     named in the log rather than passed on.
/// </summary>
public sealed record CanvasWorkflowImportSnapshot(IReadOnlyList<CanvasWorkflowImportCandidate> Candidates, int FailedCount);

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
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        if (tables.Count == 0)
        {
            return new CanvasWorkflowImportSnapshot([], FailedCount: 0);
        }

        var rows = await dbContext.Database
                                  .SqlQueryRaw<CanvasWorkflowRow>("SELECT id AS Id, name AS Name, graph_json AS GraphJson, created_at_utc AS CreatedAtUtc "
                                                                  + "FROM canvas_workflows ORDER BY created_at_utc ASC")
                                  .ToListAsync(cancellationToken)
                                  .ConfigureAwait(false);

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

                candidates.Add(new CanvasWorkflowImportCandidate(row.Id, row.Name ?? string.Empty, plaintext, row.CreatedAtUtc));
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

        return new CanvasWorkflowImportSnapshot(candidates, failed);
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

            var outcome = await ImportOneAsync(definitions, store, candidate, logger, cancellationToken).ConfigureAwait(false);
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
    /// </summary>
    internal static ImportMapResult MapGraph(string graphJson)
    {
        var reasons = new List<string>();
        var canvas = ReadCanvas(graphJson, reasons);

        var nodes = canvas.Nodes ?? [];
        var edges = canvas.Edges ?? [];

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

            mappedNodes.Add(MapNode(node, key, canvas.StartText, reasons));
        }

        var mappedEdges = MapEdges(edges, elided, keyByCanvasId, pauseKeys, used, reasons);
        return new ImportMapResult(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["nodes"] = mappedNodes,
                ["edges"] = mappedEdges
            },

            // Deduplicated: one dropped-edge complaint says as much as twenty, and this text reaches a 1024-character
            // description column.
            [.. reasons.Distinct(StringComparer.Ordinal)]);
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
        var map = MapGraph(candidate.GraphJson);
        var graphJson = map.Document.ToJsonString();
        var name = DefinitionName(candidate.Name);
        var provenance = $"Imported from Open Canvas (canvas workflow {candidate.Id}).";

        try
        {
            var created = await definitions.CreateAsync(name, provenance, graphJson, cancellationToken).ConfigureAwait(false);
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
            return await SaveUnvalidatedAsync(store, candidate, map, graphJson, name, provenance, refusal, logger, cancellationToken).ConfigureAwait(false);
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
            var created = await store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(Guid.NewGuid(), name, graphJson, nodeCount, Description: description),
                                         cancellationToken)
                                     .ConfigureAwait(false);

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
                    ["defaultInput"] = string.IsNullOrEmpty(startText) ? null : JsonValue.Create(startText)
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

            foreach (var targetId in ResolveTargets(edge.TargetId ?? string.Empty, elided, successors, strandedDebug))
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

        foreach (var stranded in strandedDebug.Order(StringComparer.Ordinal))
        {
            reasons.Add($"A canvas Debug node ('{Sanitize(stranded)}') had nothing after it, so the path into it now ends nowhere.");
        }

        return mapped;
    }

    /// <summary>
    ///     Where an edge into <paramref name="targetId" /> really lands: itself, or — when it is an elided Debug node —
    ///     whatever that node pointed at, transitively. A Debug node with no successor is recorded rather than
    ///     silently swallowing the edge.
    /// </summary>
    private static List<string> ResolveTargets(string targetId,
        HashSet<string> elided,
        Dictionary<string, List<string>> successors,
        HashSet<string> stranded)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
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
                continue;
            }

            foreach (var successor in next)
            {
                pending.Push(successor);
            }
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

    private sealed record CanvasGraph(string? StartText, IReadOnlyList<CanvasNode>? Nodes, IReadOnlyList<CanvasEdge>? Edges);

    private sealed record CanvasNode(string? Id, string? Kind, string? Label, string? Instructions, string? Model, string? ModelProfile, string? ReasoningEffort);

    private sealed record CanvasEdge(string? SourceId, string? TargetId);
}

/// <summary>
///     What the mapper made of one canvas graph. <see cref="Reasons" /> is what it could not translate faithfully —
///     empty for a graph that came across whole.
/// </summary>
internal sealed record ImportMapResult(JsonNode Document, IReadOnlyList<string> Reasons);
