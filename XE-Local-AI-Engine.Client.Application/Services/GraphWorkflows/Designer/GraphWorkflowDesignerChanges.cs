namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

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
        if (TryParse(workingGraphJson, maxNodes) is { } working && TryParse(proposalGraphJson, maxNodes) is { } proposal)
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

    private static GraphWorkflowGraph? TryParse(string graphJson, int maxNodes)
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
        JsonNode? root;
        try
        {
            root = string.IsNullOrWhiteSpace(graphJson) ? null : JsonNode.Parse(graphJson);
        }
        catch (JsonException)
        {
            return elements;
        }

        if (root is not JsonObject document || document[member] is not JsonArray array)
        {
            return elements;
        }

        foreach (var entry in array.OfType<JsonObject>())
        {
            if (entry["key"] is JsonValue key && key.TryGetValue<string>(out var name) && !elements.ContainsKey(name))
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
