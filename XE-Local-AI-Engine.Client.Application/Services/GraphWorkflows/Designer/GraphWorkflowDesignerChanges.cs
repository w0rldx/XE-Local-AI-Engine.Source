namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>What a proposal changes against the working graph: its settings (<c>graph</c>/<c>settings</c>), then node and edge by key.</summary>
/// <remarks>
///     Compared on the PARSED graphs, so a member the model wrote out at its parser default (<c>model: null</c>,
///     <c>questionType: "choice"</c>) is no change. A <c>position</c> is no change either: the runtime never reads it.
///     When either side does not parse — an empty canvas, a half-drawn graph, an invalid proposal — the raw documents are
///     compared instead, minus positions.
/// </remarks>
internal static class GraphWorkflowDesignerChanges
{
    private const string Node = "node";
    private const string Edge = "edge";

    private static readonly GraphWorkflowDesignerChange SettingsModified = new()
    {
        Kind = "graph",
        Key = "settings",
        Change = "modified"
    };

    private static readonly JsonSerializerOptions ConfigOptions = JsonSerializerOptions.Web;

    public static IReadOnlyList<GraphWorkflowDesignerChange> Compute(string workingGraphJson, string proposalGraphJson, int maxNodes)
    {
        if (ParseOrNull(workingGraphJson, maxNodes) is { } working && ParseOrNull(proposalGraphJson, maxNodes) is { } proposal)
        {
            return
            [
                .. SettingsDiff(new GraphSettings(working.Kind, working.Chat), new GraphSettings(proposal.Kind, proposal.Chat)),
                .. Diff(Node, working.Nodes, proposal.Nodes, SameNode),
                .. Diff(Edge, working.Edges.ToDictionary(static edge => edge.Key, StringComparer.Ordinal), proposal.Edges.ToDictionary(static edge => edge.Key, StringComparer.Ordinal), SameEdge)
            ];
        }

        return
        [
            .. SettingsDiff(RawSettings(workingGraphJson), RawSettings(proposalGraphJson)),
            .. Diff(Node, RawElements(workingGraphJson, "nodes"), RawElements(proposalGraphJson, "nodes"), JsonNode.DeepEquals),
            .. Diff(Edge, RawElements(workingGraphJson, "edges"), RawElements(proposalGraphJson, "edges"), JsonNode.DeepEquals)
        ];
    }

    /// <summary>
    ///     The proposal with its edge keys aligned to the working graph, so the key-based diff reports the connections that
    ///     really changed: a model that reuses an existing key for a new connection would otherwise shift every key after it.
    /// </summary>
    /// <remarks>
    ///     A proposal edge keeps a working key only for the same <c>from</c>/<c>to</c>: first an edge that already carries
    ///     it, then the next unclaimed working edge between the same nodes, one to one in order, so parallel edges pair up.
    ///     Any other edge whose key is a working edge's key or repeats an earlier proposal edge's gets the next free
    ///     <c>e&lt;n&gt;</c> above every number in use. A <c>sourceHandle</c> is not compared: the proposal carries none.
    /// </remarks>
    public static GraphWorkflowDesignerGraph NormalizeEdgeKeys(string? workingGraphJson, GraphWorkflowDesignerGraph proposal)
    {
        if (proposal.Edges is not { Count: > 0 } edges)
        {
            return proposal;
        }

        var working = RawEdges(workingGraphJson);
        var keys = new string?[edges.Count];
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < edges.Count; i++)
        {
            if (edges[i] is { Key: { } key } edge && working.Any(entry => entry.Key == key && SameEnds(entry, edge)) && claimed.Add(key))
            {
                keys[i] = key;
            }
        }

        for (var i = 0; i < edges.Count; i++)
        {
            if (keys[i] is null && edges[i] is { } edge && working.FirstOrDefault(entry => !claimed.Contains(entry.Key) && SameEnds(entry, edge)) is { Key: { } key })
            {
                _ = claimed.Add(key);
                keys[i] = key;
            }
        }

        // Every key either document names, so a fresh key never lands on a node or on a later edge that keeps its own.
        var used = new HashSet<string>(working.Select(static entry => entry.Key), StringComparer.Ordinal);
        used.UnionWith(RawKeys(workingGraphJson, "nodes"));
        used.UnionWith((proposal.Nodes ?? []).Select(static node => node?.Key).OfType<string>());
        used.UnionWith(edges.Select(static edge => edge?.Key).OfType<string>());
        var next = used.Select(static key => key.Length > 1 && key[0] == 'e' && long.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
                       .DefaultIfEmpty(0)
                       .Max() + 1;
        var workingKeys = working.Select(static entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < edges.Count; i++)
        {
            if (keys[i] is not null || edges[i] is not { Key: { } key })
            {
                continue;
            }

            if (workingKeys.Contains(key) || claimed.Contains(key))
            {
                while (used.Contains($"e{next}"))
                {
                    next++;
                }

                key = $"e{next++}";
            }

            _ = claimed.Add(key);
            keys[i] = key;
        }

        return new GraphWorkflowDesignerGraph
        {
            SchemaVersion = proposal.SchemaVersion,
            Kind = proposal.Kind,
            Chat = proposal.Chat,
            Nodes = proposal.Nodes,
            Edges =
            [
                .. edges.Select((edge, i) => keys[i] is not { } key || key == edge.Key
                    ? edge
                    : new GraphWorkflowDesignerEdge
                    {
                        Key = key,
                        From = edge.From,
                        To = edge.To,
                        Label = edge.Label,
                        Condition = edge.Condition
                    })
            ]
        };

        static bool SameEnds(WorkingEdge entry, GraphWorkflowDesignerEdge edge) =>
            string.Equals(entry.From, edge.From, StringComparison.Ordinal) && string.Equals(entry.To, edge.To, StringComparison.Ordinal);
    }

    /// <summary>The working graph's keyed edges in document order; empty when it cannot be read.</summary>
    private static List<WorkingEdge> RawEdges(string? graphJson)
    {
        var edges = new List<WorkingEdge>();
        foreach (var entry in RawArray(graphJson, "edges"))
        {
            if (RawString(entry, "key") is { } key)
            {
                edges.Add(new WorkingEdge(key, RawString(entry, "from"), RawString(entry, "to")));
            }
        }

        return edges;
    }

    private static IEnumerable<string> RawKeys(string? graphJson, string member) =>
        RawArray(graphJson, member).Select(static entry => RawString(entry, "key")).OfType<string>();

    private static string? RawString(JsonObject entry, string member) =>
        entry[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IEnumerable<JsonObject> RawArray(string? graphJson, string member)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(graphJson) && JsonNode.Parse(graphJson) is JsonObject document && document[member] is JsonArray array
                ? array.OfType<JsonObject>().ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>One of the working graph's edges, as the key normalization compares it.</summary>
    private readonly record struct WorkingEdge(string Key, string? From, string? To);

    /// <summary>One change when the graph kind or the chat block differs; both sides are already defaulted, so a default written out is equal.</summary>
    private static IEnumerable<GraphWorkflowDesignerChange> SettingsDiff(GraphSettings working, GraphSettings proposal) =>
        working == proposal ? [] : [SettingsModified];

    /// <summary>The settings of a raw document, defaulted as the parser defaults them; an unreadable kind compares by its text.</summary>
    private static GraphSettings RawSettings(string graphJson)
    {
        JsonObject? root;
        try
        {
            root = string.IsNullOrWhiteSpace(graphJson) ? null : JsonNode.Parse(graphJson) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        var kindText = root?["kind"] is JsonValue kindValue && kindValue.TryGetValue<string>(out var named) ? named : null;
        var kind = GraphWorkflowDefinitionKind.Standard;
        if (kindText is not null && !GraphWorkflowTokens.TryParseName(kindText, out kind))
        {
            return new GraphSettings(Kind: null, Chat: null, UnreadKind: kindText);
        }

        if (kind != GraphWorkflowDefinitionKind.Chat)
        {
            return new GraphSettings(kind, Chat: null);
        }

        var chat = root?["chat"] as JsonObject;
        return new GraphSettings(kind, new GraphWorkflowChatSettings
        {
            AcceptsAttachments = RawBool(chat, "acceptsAttachments") ?? GraphWorkflowChatSettings.Default.AcceptsAttachments,
            RequireRerunConfirmation = RawBool(chat, "requireRerunConfirmation") ?? GraphWorkflowChatSettings.Default.RequireRerunConfirmation
        });
    }

    private static bool? RawBool(JsonObject? owner, string member) =>
        owner?[member] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static IEnumerable<GraphWorkflowDesignerChange> Diff<T>(string kind,
        IReadOnlyDictionary<string, T> working,
        IReadOnlyDictionary<string, T> proposal,
        Func<T, T, bool> same)
    {
        foreach (var key in working.Keys.Union(proposal.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var change = (working.TryGetValue(key, out var before), proposal.TryGetValue(key, out var after)) switch
            {
                (false, true) => "added",
                (true, false) => "removed",
                _ => same(before!, after!) ? null : "modified"
            };
            if (change is not null)
            {
                yield return new GraphWorkflowDesignerChange
                {
                    Kind = kind,
                    Key = key,
                    Change = change
                };
            }
        }
    }

    private static bool SameNode(GraphWorkflowGraphNode before, GraphWorkflowGraphNode after) =>
        before.Kind == after.Kind
        && string.Equals(before.Label, after.Label, StringComparison.Ordinal)
        && before.JoinPolicy == after.JoinPolicy
        && before.MaxAttempts == after.MaxAttempts
        && before.TimeoutSeconds == after.TimeoutSeconds
        && JsonElement.DeepEquals(SerializeConfig(before.Config), SerializeConfig(after.Config));

    private static bool SameEdge(GraphWorkflowGraphEdge before, GraphWorkflowGraphEdge after) =>
        string.Equals(before.From, after.From, StringComparison.Ordinal)
        && string.Equals(before.To, after.To, StringComparison.Ordinal)
        && string.Equals(before.Label, after.Label, StringComparison.Ordinal)
        && (before.Condition, after.Condition) switch
        {
            (null, null) => true,
            ({ } a, { } b) => string.Equals(a.Path, b.Path, StringComparison.Ordinal) && a.Operator == b.Operator && JsonElement.DeepEquals(a.Value, b.Value),
            _ => false
        };

    /// <summary>A config record as JSON, so the collection and raw-JSON members compare by content rather than by reference.</summary>
    private static JsonElement SerializeConfig(GraphWorkflowNodeConfig config) =>
        JsonSerializer.SerializeToElement(config, config.GetType(), ConfigOptions);

    private static GraphWorkflowGraph? ParseOrNull(string graphJson, int maxNodes)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
        {
            return null;
        }

        try
        {
            // Per-element errors are tolerated: the graph handed back still routes, and its keys are what is compared.
            return GraphWorkflowGraph.Parse(graphJson, maxNodes, []);
        }
        catch (GraphWorkflowValidationException)
        {
            return null;
        }
    }

    /// <summary>The keyed entries of one array of a raw graph document, positions removed; empty when it cannot be read.</summary>
    private static Dictionary<string, JsonNode?> RawElements(string graphJson, string member)
    {
        var elements = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var entry in RawArray(graphJson, member))
        {
            if (RawString(entry, "key") is { } name && !elements.ContainsKey(name))
            {
                var copy = entry.DeepClone().AsObject();
                _ = copy.Remove("position");
                elements[name] = copy;
            }
        }

        return elements;
    }

    /// <summary>A graph's own settings; <see cref="UnreadKind" /> keeps the text of a kind the parser would refuse.</summary>
    private readonly record struct GraphSettings(GraphWorkflowDefinitionKind? Kind, GraphWorkflowChatSettings? Chat, string? UnreadKind = null);
}
