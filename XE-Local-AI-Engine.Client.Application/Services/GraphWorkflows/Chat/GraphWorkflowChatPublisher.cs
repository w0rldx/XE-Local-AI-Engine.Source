namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The dispatcher's publish outbox pass: every succeeded <c>publishToChat</c> node of a chat-bound run becomes one
///     Completed assistant message in the conversation, stamped on the node run once written.
/// </summary>
/// <remarks>
///     Idempotent in either crash order: the message id is deterministic in <c>(run, node, attempt)</c> and inserted
///     if absent, and the stamp is a compare-and-set. A failure is logged and retried next tick; it never fails the node.
///     The dispatcher holds a run's recompute back for a few ticks while a publish keeps failing, so a run does not end on one.
/// </remarks>
internal sealed class GraphWorkflowChatPublisher
{
    private readonly ILogger<GraphWorkflowChatPublisher> _logger;
    private readonly INodeChatPersistenceService _persistence;
    private readonly TimeProvider _timeProvider;

    public GraphWorkflowChatPublisher(INodeChatPersistenceService persistence, TimeProvider timeProvider, ILogger<GraphWorkflowChatPublisher> logger)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Publishes what is due and answers how many node runs it stamped and how many it could not publish.</summary>
    public async Task<(int Written, int Failed)> PublishAsync(IGraphWorkflowStore store, GraphWorkflowRunSnapshot run, GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(graph);
        if (run.ConversationId is not { } conversationId)
        {
            return (0, 0);
        }

        var written = 0;
        var failed = 0;
        foreach (var nodeRun in await store.ListUnpublishedNodeRunsAsync(run.Id, cancellationToken))
        {
            if (!graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node) || !PublishesToChat(node.Config))
            {
                continue;
            }

            try
            {
                var messageId = GraphWorkflowChatIds.PublishedMessage(run.Id, nodeRun.NodeKey, nodeRun.Attempt);
                _ = await _persistence.InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
                    {
                        ConversationId = conversationId,
                        MessageId = messageId,
                        Role = "assistant",
                        Content = ContentOf(node, nodeRun.OutputJson),
                        CreatedAtUtc = nodeRun.CompletedAtUtc ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        Model = TextAt(nodeRun.OutputJson, "output.usage.model"),
                        AgentDefinitionId = (node.Config as GraphWorkflowAgentConfig)?.AgentDefinitionId,
                        AgentName = node.Label
                    },
                    cancellationToken);
                written += await store.MarkNodeRunPublishedAsync(run.Id, nodeRun.Id, messageId, cancellationToken) is null ? 0 : 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(exception, "Graph workflow run {RunId} could not publish node {NodeKey} to its conversation; retrying next tick.", run.Id, nodeRun.NodeKey);
            }
        }

        return (written, failed);
    }

    private static bool PublishesToChat(GraphWorkflowNodeConfig config) =>
        config switch
        {
            GraphWorkflowAgentConfig agent => agent.PublishToChat,
            GraphWorkflowLlmCallConfig llmCall => llmCall.PublishToChat,
            GraphWorkflowEndConfig end => end.PublishToChat,
            _ => false
        };

    /// <summary>An answer's text; an End's result when it is a string, else fenced JSON — never empty, which the insert refuses.</summary>
    private static string ContentOf(GraphWorkflowGraphNode node, string? outputJson)
    {
        if (node.Kind != GraphWorkflowNodeKind.End && TextAt(outputJson, "output.text") is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var value = GraphWorkflowDocuments.Resolve(outputJson, node.Kind == GraphWorkflowNodeKind.End ? "output.result" : "output");
        if (value is { ValueKind: JsonValueKind.String } result && !string.IsNullOrWhiteSpace(result.GetString()))
        {
            return result.GetString()!;
        }

        return $"```json\n{(value is { } json ? json.GetRawText() : "null")}\n```";
    }

    private static string? TextAt(string? json, string path) =>
        GraphWorkflowDocuments.Resolve(json, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
