namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Conversations, sends and reads for the chat-workflow suites, over the real persistence and the real routes.</summary>
internal static class GraphWorkflowChatTestSupport
{
    public const string Root = "/api/local/v1/graph-workflows";

    /// <summary>A Chat graph parked on one ChatInput, whose End publishes the answer text; the two chat settings are the knobs.</summary>
    public static string ChatInputGraph(bool acceptsAttachments = false, bool requireRerunConfirmation = true) =>
        $$"""
          {
            "schemaVersion": 1,
            "kind": "Chat",
            "chat": { "acceptsAttachments": {{Json(acceptsAttachments)}}, "requireRerunConfirmation": {{Json(requireRerunConfirmation)}} },
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "ask", "kind": "ChatInput", "config": { "prompt": "Which database?" } },
              { "key": "done", "kind": "End", "label": "Answer", "config": { "outcome": "completed", "resultPath": "input.output.text" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "ask" },
              { "key": "e2", "from": "ask", "to": "done" }
            ]
          }
          """;

    /// <summary>A Chat graph parked on a Pause rather than a ChatInput — busy to a chat send.</summary>
    public const string ChatPauseGraph = """
                                         {
                                           "schemaVersion": 1,
                                           "kind": "Chat",
                                           "nodes": [
                                             { "key": "start", "kind": "Start" },
                                             { "key": "review", "kind": "Pause", "config": { "prompt": "Approve?", "allowedDecisions": ["Approve", "Reject"] } },
                                             { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
                                           ],
                                           "edges": [
                                             { "key": "e1", "from": "start", "to": "review" },
                                             { "key": "e2", "from": "review", "to": "done" }
                                           ]
                                         }
                                         """;

    /// <summary>A Chat graph with one publishing Agent node whose instructions the fake runner scripts on.</summary>
    public static string ChatAgentGraph(string instructions) =>
        $$"""
          {
            "schemaVersion": 1,
            "kind": "Chat",
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "analyze", "kind": "Agent", "label": "Analyst", "config": { "instructions": "{{instructions}}", "publishToChat": true } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed", "publishToChat": false } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "analyze" },
              { "key": "e2", "from": "analyze", "to": "done" }
            ]
          }
          """;

    public static async Task<Guid> CreateConversationAsync(IServiceProvider services, string kind = NodeConversationKind.Chat, string origin = NodeChatOriginValues.Local)
    {
        var persistence = services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "workflow chat", UserId = "node", CreatedAtUtc = 1, Kind = kind, Origin = origin });
        return conversation.ConversationId;
    }

    public static async Task<IReadOnlyList<NodeChatPersistedMessageDto>> MessagesAsync(IServiceProvider services, Guid conversationId) =>
        (await services.GetRequiredService<INodeChatPersistenceService>().GetConversationAsync(conversationId))?.Messages
        ?? throw new AssertionException($"Conversation {conversationId} does not exist.");

    public static string Body(Guid definitionId, string content, Guid? requestId = null, IReadOnlyList<Guid>? attachmentFileIds = null, bool? confirmRerun = null) =>
        JsonSerializer.Serialize(new
        {
            requestId = requestId ?? Guid.NewGuid(),
            definitionId,
            content,
            attachmentFileIds,
            confirmRerun
        });

    public static async Task<HttpResponseMessage> PostMessageAsync(TestServerWebAppFactory factory, Guid conversationId, string body)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/conversations/{conversationId}/messages");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> PostSteerAsync(TestServerWebAppFactory factory, Guid runId, string nodeKey, Guid operationId, string message)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/runs/{runId}/nodes/{nodeKey}/steer");
        request.Content = new StringContent(JsonSerializer.Serialize(new { operationId, message }), Encoding.UTF8, "application/json");
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> GetRunsAsync(TestServerWebAppFactory factory, Guid conversationId)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Root}/conversations/{conversationId}/runs");
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public static async Task<string?> ConflictTypeAsync(HttpResponseMessage response)
    {
        using var document = await ReadJsonAsync(response);
        return document.RootElement.TryGetProperty("conflictType", out var type) ? type.GetString() : null;
    }

    private static string Json(bool value) =>
        value ? "true" : "false";
}
