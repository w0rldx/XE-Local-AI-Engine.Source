namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The chat-workflow contract through the real routes and store: the denormalized definition <c>kind</c> and the answer route.</summary>
/// <remarks>The graph-level <c>kind</c>/<c>chat</c> survive the wire mapper, and a Standard graph gains neither.</remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowChatDefinitionEndpointTests
{
    private const string Root = "/api/local/v1/graph-workflows";

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    public async Task AChatDefinition_RoundTripsItsKindThroughCreateListGetAndUpdate()
    {
        var name = $"Chat round trip {Guid.NewGuid():N}";
        using var created = await SendAsync("POST", $"{Root}/definitions", Body(name, GraphWorkflowGraphs.ChatInputAnswer));
        var createdBody = await created.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.Created, created.StatusCode, createdBody);
        using var definition = JsonDocument.Parse(createdBody);
        var id = definition.RootElement.GetProperty("id").GetGuid();
        AssertEx.Equal("Chat", definition.RootElement.GetProperty("kind").GetString());
        var graph = definition.RootElement.GetProperty("graph");
        AssertEx.Equal("Chat", graph.GetProperty("kind").GetString(), "the graph-level kind survives the wire mapper.");
        AssertEx.True(graph.GetProperty("chat").GetProperty("requireRerunConfirmation").GetBoolean(), "and so does the chat block.");

        using var list = await ReadJsonAsync($"{Root}/definitions");
        var listed = list.RootElement.GetProperty("definitions").EnumerateArray().Single(entry => entry.GetProperty("id").GetGuid() == id);
        AssertEx.Equal("Chat", listed.GetProperty("kind").GetString(), "the picker reads the kind off the list without a graph.");

        using var detail = await ReadJsonAsync($"{Root}/definitions/{id}");
        AssertEx.Equal("Chat", detail.RootElement.GetProperty("kind").GetString());

        var version = detail.RootElement.GetProperty("version").GetInt32();
        using var updated = await SendAsync("PUT",
                $"{Root}/definitions/{id}",
                $$"""{"version":{{version}},"graph":{{GraphWorkflowGraphs.StartAgentEnd}}}""");
        var updatedBody = await updated.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, updated.StatusCode, updatedBody);
        using var standard = JsonDocument.Parse(updatedBody);
        AssertEx.Equal("Standard", standard.RootElement.GetProperty("kind").GetString(), "a new graph re-derives the kind with it.");
        AssertEx.True(IsAbsent(standard.RootElement.GetProperty("graph"), "kind"), "a Standard graph that named no kind gains none on the way out.");
        AssertEx.True(IsAbsent(standard.RootElement.GetProperty("graph"), "chat"));
    }

    [Test]
    public async Task AChatBlockOnAStandardGraph_IsRefusedAtSave()
    {
        var graph = GraphWorkflowGraphs.StartAgentEnd.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"chat\": { \"acceptsAttachments\": true },", StringComparison.Ordinal);

        using var response = await SendAsync("POST", $"{Root}/definitions", Body($"Refused {Guid.NewGuid():N}", graph));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "apply to a Chat graph only");
    }

    [Test]
    public async Task AParkedChatInput_TakesItsAnswerThroughTheDecideRoute()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.ChatInputAnswer);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        using var empty = await SendAsync("POST", Decide(runId), DecisionBody(Guid.NewGuid(), "Answer", new { text = "" }));
        AssertEx.Equal(HttpStatusCode.BadRequest, empty.StatusCode, "an empty answer is the request's fault.");

        using var approve = await SendAsync("POST", Decide(runId), DecisionBody(Guid.NewGuid(), "Approve", payload: null));
        AssertEx.Equal(HttpStatusCode.Conflict, approve.StatusCode, "a chat input is not a gate.");

        using var answered = await SendAsync("POST", Decide(runId), DecisionBody(Guid.NewGuid(), "Answer", new { text = "postgres" }));
        var body = await answered.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, answered.StatusCode, body);
        using var result = JsonDocument.Parse(body);
        AssertEx.Equal("Answer", result.RootElement.GetProperty("decision").GetString());
        AssertEx.Equal("Succeeded", result.RootElement.GetProperty("nodeRunStatus").GetString());
    }

    /// <summary>Absent or JSON null: the response serializer writes a null member, the stored document omits it.</summary>
    private static bool IsAbsent(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static string Body(string name, string graph) =>
        $$"""{"name":"{{name}}","description":null,"graph":{{graph}}}""";

    private static string Decide(Guid runId) =>
        $"{Root}/runs/{runId}/nodes/ask/decide";

    private static string DecisionBody(Guid operationId, string decision, object? payload) =>
        JsonSerializer.Serialize(new { operationId, decision, payload });

    private async Task<JsonDocument> ReadJsonAsync(string route)
    {
        using var response = await SendAsync("GET", route, body: null);
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string route, string? body)
    {
        using var client = Host.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        Host.Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }
}
