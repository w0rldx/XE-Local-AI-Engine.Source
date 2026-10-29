namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The designer turn on the wire: request caps, typed refusals and the response shape the editor draws.</summary>
/// <remarks>Two tests run the real service in the host (validator, cloud gate); the rest substitute it to assert each mapping alone.</remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowDesignerEndpointTests
{
    private const string Turn = "/api/local/v1/graph-workflows/designer/turn";

    [Test]
    public async Task Turn_WhenTheLastMessageIsNotTheOperators_Answers400()
    {
        await using var factory = Factory(service: null);

        using var response = await SendAsync(factory, Body("assistant"));

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Turn_WhenAMessageIsNull_Answers400NamingIt()
    {
        await using var factory = Factory(service: null);
        var body = JsonSerializer.Serialize(new
        {
            modelName = "qwen3-8b:Q4_K_M",
            messages = new object?[] { null, new { role = "user", text = "Add a review step." } },
            graph = JsonDocument.Parse(GraphWorkflowGraphs.StartAgentEnd).RootElement
        });

        using var response = await SendAsync(factory, body);
        var text = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, text);
        AssertEx.Contains(text, "A message cannot be null.");
    }

    /// <summary>The real service in the real host: a model that leaves the node, with no grant and the switch off, is refused before any call.</summary>
    [Test]
    public async Task Turn_OnACloudModelWithoutTheLocalDataGrant_Answers403NamingTheGrant()
    {
        await using var factory = Factory(service: null);

        using var response = await SendAsync(factory, Body("user", model: "ext:00000000-0000-0000-0000-000000000000/remote-model"));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("CloudLocalDataWithheld", document.RootElement.GetProperty("code").GetString());
        AssertEx.Equal("LocalData", document.RootElement.GetProperty("grant").GetString());
    }

    [Test]
    public async Task Turn_WithAProposal_Answers200WithTheProposalChangesValidationAndMetadata()
    {
        var service = Substitute.For<IGraphWorkflowDesignerService>();
        service.TurnAsync(Arg.Any<GraphWorkflowDesignerTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(GraphWorkflowDesignerResult.Success(new GraphWorkflowDesignerTurn
               {
                   Reply = "Added a review.",
                   ProposalGraphJson = GraphWorkflowGraphs.StartAgentEnd,
                   Changes =
                   [
                       new GraphWorkflowDesignerChange
                       {
                           Kind = "node",
                           Key = "analyze",
                           Change = "added"
                       }
                   ],
                   Validation = GraphWorkflowValidationResult.ValidWith([new GraphWorkflowValidationError("analyze", "a lint warning")]),
                   Attempts = 2,
                   Model = "qwen3-8b:Q4_K_M",
                   GeneratedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_000),
                   DurationMs = 1234,
                   InputTokens = 100,
                   OutputTokens = 20
               }));
        await using var factory = Factory(service);

        using var response = await SendAsync(factory, Body("user", intent: "change", selectedNodeKey: "analyze"));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        AssertEx.Equal("Added a review.", root.GetProperty("reply").GetString());
        AssertEx.Equal(3, root.GetProperty("proposal").GetProperty("nodes").GetArrayLength());
        var change = root.GetProperty("changes")[0];
        AssertEx.Equal("node", change.GetProperty("kind").GetString());
        AssertEx.Equal("analyze", change.GetProperty("key").GetString());
        AssertEx.Equal("added", change.GetProperty("change").GetString());
        var validation = root.GetProperty("validation");
        AssertEx.True(validation.GetProperty("valid").GetBoolean());
        AssertEx.Equal(3, validation.GetProperty("nodeCount").GetInt32());
        AssertEx.Equal("a lint warning", validation.GetProperty("warnings")[0].GetProperty("message").GetString());
        AssertEx.Equal(2, root.GetProperty("attempts").GetInt32());
        var metadata = root.GetProperty("generationMetadata");
        AssertEx.Equal("qwen3-8b:Q4_K_M", metadata.GetProperty("model").GetString());
        AssertEx.Equal(1_000L, metadata.GetProperty("generatedAtUtc").GetInt64());
        AssertEx.Equal(1234L, metadata.GetProperty("durationMs").GetInt64());

        await service.Received(1).TurnAsync(Arg.Is<GraphWorkflowDesignerTurnRequest>(static request => request.Intent == GraphWorkflowDesignerIntent.Change
                                                                                                         && request.SelectedNodeKey == "analyze"
                                                                                                         && request.Messages.Count == 1
                                                                                                         && request.GraphJson.Contains("\"start\"")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Turn_WithAReplyOnly_Answers200WithNullProposalAndValidation()
    {
        var service = Substitute.For<IGraphWorkflowDesignerService>();
        service.TurnAsync(Arg.Any<GraphWorkflowDesignerTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(GraphWorkflowDesignerResult.Success(new GraphWorkflowDesignerTurn
               {
                   Reply = "It summarizes.",
                   ProposalGraphJson = null,
                   Changes = [],
                   Validation = null,
                   Attempts = 1,
                   Model = "m",
                   GeneratedAtUtc = DateTimeOffset.UnixEpoch,
                   DurationMs = 1,
                   InputTokens = null,
                   OutputTokens = null
               }));
        await using var factory = Factory(service);

        using var response = await SendAsync(factory, Body("user", intent: "ask"));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(JsonValueKind.Null, document.RootElement.GetProperty("proposal").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, document.RootElement.GetProperty("validation").ValueKind);
    }

    [Test]
    public async Task Turn_OnAnEmptyChatCanvas_HandsTheServiceTheGraphKind()
    {
        var service = Substitute.For<IGraphWorkflowDesignerService>();
        service.TurnAsync(Arg.Any<GraphWorkflowDesignerTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(GraphWorkflowDesignerResult.Failed(GraphWorkflowDesignerFailureKind.Unparseable, "Refused."));
        await using var factory = Factory(service);
        var body = JsonSerializer.Serialize(new
        {
            modelName = "qwen3-8b:Q4_K_M",
            messages = new[] { new { role = "user", text = "Build a chat workflow." } },
            graph = new { schemaVersion = 1, kind = "Chat", nodes = Array.Empty<object>(), edges = Array.Empty<object>() }
        });

        using var response = await SendAsync(factory, body);

        AssertEx.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode, await response.Content.ReadAsStringAsync());
        await service.Received(1).TurnAsync(Arg.Is<GraphWorkflowDesignerTurnRequest>(static request => request.GraphJson.Contains("\"kind\":\"Chat\"")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A proposal whose model left out a node's key and kind reaches the client as empty strings, never nulls its contract refuses.</summary>
    [Test]
    public async Task Turn_WithAProposalMissingIdentityStrings_AnswersThemAsEmptyStrings()
    {
        const string Proposal = """
                                { "schemaVersion": 1,
                                  "nodes": [{ "label": "Nameless" }, { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                                  "edges": [{ "to": "done" }] }
                                """;
        var service = Substitute.For<IGraphWorkflowDesignerService>();
        service.TurnAsync(Arg.Any<GraphWorkflowDesignerTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(GraphWorkflowDesignerResult.Success(new GraphWorkflowDesignerTurn
               {
                   Reply = "Here.",
                   ProposalGraphJson = Proposal,
                   Changes = [],
                   Validation = GraphWorkflowValidationResult.Invalid([new GraphWorkflowValidationError(Key: null, "Every node needs a key.")]),
                   Attempts = 3,
                   Model = "m",
                   GeneratedAtUtc = DateTimeOffset.UnixEpoch,
                   DurationMs = 1,
                   InputTokens = null,
                   OutputTokens = null
               }));
        await using var factory = Factory(service);

        using var response = await SendAsync(factory, Body("user", intent: "change"));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var node = document.RootElement.GetProperty("proposal").GetProperty("nodes")[0];
        AssertEx.Equal(string.Empty, node.GetProperty("key").GetString());
        AssertEx.Equal(string.Empty, node.GetProperty("kind").GetString());
        var edge = document.RootElement.GetProperty("proposal").GetProperty("edges")[0];
        AssertEx.Equal(string.Empty, edge.GetProperty("key").GetString());
        AssertEx.Equal(string.Empty, edge.GetProperty("from").GetString());
        AssertEx.Equal("done", edge.GetProperty("to").GetString());
        AssertEx.False(document.RootElement.GetProperty("validation").GetProperty("valid").GetBoolean(), "the proposal stays invalid and cannot be accepted");
    }

    [Test]
    [Arguments("""{ "schemaVersion": 1, "nodes": null, "edges": null }""", 0, 0)]
    [Arguments("""{ "schemaVersion": 1, "nodes": [null, { "key": "done", "kind": "End" }], "edges": [null] }""", 1, 0)]
    public async Task Turn_WithAProposalHoldingNullArraysOrElements_MapsWithoutThrowing(string proposal, int nodes, int edges)
    {
        var service = Substitute.For<IGraphWorkflowDesignerService>();
        service.TurnAsync(Arg.Any<GraphWorkflowDesignerTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(GraphWorkflowDesignerResult.Success(new GraphWorkflowDesignerTurn
               {
                   Reply = "Here.",
                   ProposalGraphJson = proposal,
                   Changes = [],
                   Validation = GraphWorkflowValidationResult.Invalid([new GraphWorkflowValidationError(Key: null, "Broken.")]),
                   Attempts = 3,
                   Model = "m",
                   GeneratedAtUtc = DateTimeOffset.UnixEpoch,
                   DurationMs = 1,
                   InputTokens = null,
                   OutputTokens = null
               }));
        await using var factory = Factory(service);

        using var response = await SendAsync(factory, Body("user", intent: "change"));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(nodes, document.RootElement.GetProperty("proposal").GetProperty("nodes").GetArrayLength(), "null elements are dropped");
        AssertEx.Equal(edges, document.RootElement.GetProperty("proposal").GetProperty("edges").GetArrayLength());
        AssertEx.Equal(nodes, document.RootElement.GetProperty("validation").GetProperty("nodeCount").GetInt32());
    }

    [Test]
    [Arguments(GraphWorkflowDesignerFailureKind.NodeBusy, HttpStatusCode.Conflict, "NodeBusy")]
    [Arguments(GraphWorkflowDesignerFailureKind.Unparseable, HttpStatusCode.UnprocessableEntity, "Unparseable")]
    [Arguments(GraphWorkflowDesignerFailureKind.ProviderFailed, HttpStatusCode.BadGateway, "ProviderFailed")]
    public async Task Turn_WhenTheServiceRefuses_AnswersTheTypedError(GraphWorkflowDesignerFailureKind failure, HttpStatusCode status, string code)
    {
        var service = Substitute.For<IGraphWorkflowDesignerService>();
        service.TurnAsync(Arg.Any<GraphWorkflowDesignerTurnRequest>(), Arg.Any<CancellationToken>())
               .Returns(GraphWorkflowDesignerResult.Failed(failure, "Refused."));
        await using var factory = Factory(service);

        using var response = await SendAsync(factory, Body("user"));
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(status, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(code, document.RootElement.GetProperty("code").GetString());
        AssertEx.Equal("Refused.", document.RootElement.GetProperty("message").GetString());
    }

    private static string Body(string lastRole, string model = "qwen3-8b:Q4_K_M", string? intent = null, string? selectedNodeKey = null) =>
        JsonSerializer.Serialize(new
        {
            modelName = model,
            messages = new[] { new { role = lastRole, text = "Add a review step." } },
            graph = JsonDocument.Parse(GraphWorkflowGraphs.StartAgentEnd).RootElement,
            intent,
            selectedNodeKey
        });

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string body)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Turn)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    private static TestServerWebAppFactory Factory(IGraphWorkflowDesignerService? service) =>
        new()
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "true"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IGraphWorkflowStore>();
                services.AddSingleton(Substitute.For<IGraphWorkflowStore>());
                if (service is not null)
                {
                    services.RemoveAll<IGraphWorkflowDesignerService>();
                    services.AddSingleton(service);
                }
            }
        };
}
