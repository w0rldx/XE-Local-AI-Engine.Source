namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The share-file pair: export a definition as a download, import that file back through create's own path.</summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowShareFileEndpointTests
{
    private const string Definitions = "/api/local/v1/graph-workflows/definitions";
    private const string Definition = $"{Definitions}/22222222-2222-2222-2222-222222222222";
    private const string Export = $"{Definition}/export";
    private const string Import = $"{Definitions}/import";

    private static readonly Guid DefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    [Arguments("GET", Export)]
    [Arguments("POST", Import)]
    public async Task Route_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = EnabledFactory(Store());
        using var client = factory.CreateClient();
        using var request = Request(method, route, ImportBody(GraphWorkflowGraphs.StartAgentEnd));

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
    }

    [Test]
    [Arguments("GET", Export)]
    [Arguments("POST", Import)]
    public async Task Route_WithANonOperatorToken_ReturnsForbidden(string method, string route)
    {
        await using var factory = EnabledFactory(Store());
        using var client = factory.CreateClient();
        using var request = Request(method, route, ImportBody(GraphWorkflowGraphs.StartAgentEnd));
        factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, $"{method} {route} is operator-only.");
    }

    [Test]
    [Arguments("GET", Export)]
    [Arguments("POST", Import)]
    public async Task Route_WhenTheFeatureIsDisabled_ReturnsNotFoundWithoutReachingTheStore(string method, string route)
    {
        var store = Store();
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IGraphWorkflowStore>();
                services.AddSingleton(store);
            }
        };

        using var response = await SendAsync(factory, method, route, ImportBody(GraphWorkflowGraphs.StartAgentEnd));

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 on a disabled node.");
        AssertEx.Empty(store.ReceivedCalls());
    }

    [Test]
    public async Task Export_AnswersTheEnvelopeWithTheDetailRoutesGraphAsAnAttachment()
    {
        var store = Store();
        store.GetDefinitionAsync(DefinitionId, Arg.Any<CancellationToken>()).Returns(Snapshot("Übersicht für Märkte", GraphWorkflowGraphs.ToolNode));
        await using var factory = EnabledFactory(store);

        using var detail = await SendAsync(factory, "GET", Definition);
        using var export = await SendAsync(factory, "GET", Export);
        var detailBody = await detail.Content.ReadAsStringAsync();
        var exportBody = await export.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.OK, export.StatusCode, exportBody);
        AssertEx.Equal("attachment; filename=\"ubersicht-fur-markte.graph-workflow.json\"",
            export.Content.Headers.ContentDisposition?.ToString(),
            "the download is named after the definition, accents folded to ASCII.");
        using var envelope = JsonDocument.Parse(exportBody);
        var root = envelope.RootElement;
        AssertEx.Equal("xe-graph-workflow", root.GetProperty("format").GetString());
        AssertEx.Equal(1, root.GetProperty("formatVersion").GetInt32());
        AssertEx.Equal("Übersicht für Märkte", root.GetProperty("name").GetString());
        AssertEx.Equal("The one that triages.", root.GetProperty("description").GetString());
        AssertEx.Equal(5, root.EnumerateObject().Count(), $"the envelope carries nothing beyond its five members, so no id or version leaks into a shared file: {exportBody}");
        using var detailDocument = JsonDocument.Parse(detailBody);
        AssertEx.Equal(detailDocument.RootElement.GetProperty("graph").GetRawText(),
            root.GetProperty("graph").GetRawText(),
            "the exported graph is the detail route's graph, byte for byte.");
    }

    /// <summary>
    ///     A shared graph never brings authority with it (ADR 0021): neither the action flag nor the accepted trigger
    ///     sources are exported or imported; an import gets the defaults.
    /// </summary>
    [Test]
    public async Task AFlaggedDefinition_ExportsWithoutTheFlag_AndAnImportCarryingItCreatesWithoutIt()
    {
        var store = Store();
        store.GetDefinitionAsync(DefinitionId, Arg.Any<CancellationToken>())
             .Returns(Snapshot("Triage", GraphWorkflowGraphs.StartAgentEnd, allowsActions: true, triggerSources: GraphWorkflowTriggerSource.Schedule | GraphWorkflowTriggerSource.Intent));
        CreateGraphWorkflowDefinitionCommand? command = null;
        store.CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 command = call.Arg<CreateGraphWorkflowDefinitionCommand>();
                 return Snapshot(command.Name, command.GraphJson);
             });
        await using var factory = EnabledFactory(store);

        using var export = await SendAsync(factory, "GET", Export);
        var exportBody = await export.Content.ReadAsStringAsync();
        using var imported = await SendAsync(factory, "POST", Import,
            ImportBody(GraphWorkflowGraphs.StartAgentEnd).Replace("\"name\"", "\"allowsActions\":true,\"triggerSources\":[\"Schedule\"],\"name\"", StringComparison.Ordinal));

        AssertEx.Equal(HttpStatusCode.OK, export.StatusCode, exportBody);
        AssertEx.False(exportBody.Contains("allowsActions", StringComparison.Ordinal), $"the share file never carries the flag: {exportBody}");
        AssertEx.Equal(HttpStatusCode.Created, imported.StatusCode, await imported.Content.ReadAsStringAsync());
        AssertEx.False(exportBody.Contains("triggerSources", StringComparison.Ordinal), $"the share file never carries the trigger sources: {exportBody}");
        var created = AssertEx.NotNull(command);
        AssertEx.False(created.AllowsActions, "an import never creates a definition that may act.");
        AssertEx.Equal(GraphWorkflowTriggerSource.Manual | GraphWorkflowTriggerSource.Chat, created.TriggerSources, "an import accepts the default sources only.");
    }

    [Test]
    public async Task Export_WhenTheStoreHasNoSuchRow_ReturnsNotFound()
    {
        var store = Store();
        store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .ThrowsAsyncForAnyArgs(new GraphWorkflowNotFoundException("Graph workflow definition was not found."));
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "GET", Export);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.Null(response.Content.Headers.ContentDisposition, "a refusal is not a download.");
    }

    /// <summary>Export, import the file as downloaded, and read the new definition: its graph is the original's.</summary>
    [Test]
    public async Task Import_OfAnExportedFile_CreatesADefinitionWhoseGraphEqualsTheOriginal()
    {
        var store = Store();
        store.GetDefinitionAsync(DefinitionId, Arg.Any<CancellationToken>()).Returns(Snapshot("Triage", GraphWorkflowGraphs.ToolNode));
        CreateGraphWorkflowDefinitionCommand? command = null;
        store.CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>())
             .Returns(call =>
             {
                 command = call.Arg<CreateGraphWorkflowDefinitionCommand>();
                 return Snapshot(command.Name, command.GraphJson, Guid.Parse("33333333-3333-3333-3333-333333333333"));
             });
        await using var factory = EnabledFactory(store);
        using var detail = await SendAsync(factory, "GET", Definition);
        using var export = await SendAsync(factory, "GET", Export);

        using var response = await SendAsync(factory, "POST", Import, await export.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.Created, response.StatusCode, body);
        AssertEx.Contains(AssertEx.NotNull(response.Headers.Location).ToString(), "33333333-3333-3333-3333-333333333333");
        var stored = AssertEx.NotNull(command);
        AssertEx.Equal("Triage", stored.Name);
        AssertEx.Equal("The one that triages.", stored.Description);
        AssertEx.Equal(4, stored.NodeCount, "ToolNode is four nodes, and the count is the parser's own, so the import went through the parser.");
        using var original = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        using var imported = JsonDocument.Parse(body);
        AssertEx.Equal(original.RootElement.GetProperty("graph").GetRawText(),
            imported.RootElement.GetProperty("graph").GetRawText(),
            "the imported definition reads back the graph that was exported.");
    }

    [Test]
    [Arguments("""{"format":"something-else","formatVersion":1""", "format")]
    [Arguments("""{"format":"xe-graph-workflow","formatVersion":2""", "formatVersion")]
    public async Task Import_OfAForeignFile_Returns400KeyedToTheMemberAndNeverReachesTheStore(string head, string key)
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, "POST", Import, $$"""{{head}},"name":"Triage","graph":{{GraphWorkflowGraphs.StartAgentEnd}}}""");
        var body = await response.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("errors").EnumerateArray().Single();
        AssertEx.Equal(key, error.GetProperty("name").GetString(), $"the refusal names the member that is wrong: {body}");
        AssertEx.Empty(store.ReceivedCalls());
    }

    /// <summary>Import is create: a graph the parser refuses, or a blank name, answers the body create answers.</summary>
    [Test]
    [Arguments("Triage", "cycle")]
    [Arguments("Triage", "config-errors")]
    [Arguments(" ", "valid")]
    public async Task Import_OfAFileCreateWouldRefuse_ReturnsCreatesOwn400(string name, string graphCase)
    {
        var graph = graphCase switch
        {
            "cycle" => GraphWorkflowGraphs.StartAgentEnd.Replace("""{ "key": "e2", "from": "analyze", "to": "done" }""",
                """{ "key": "e2", "from": "analyze", "to": "done" }, { "key": "back", "from": "analyze", "to": "analyze" }""",
                StringComparison.Ordinal),
            "config-errors" => GraphWorkflowGraphs.TwoNodeConfigErrors,
            _ => GraphWorkflowGraphs.StartAgentEnd
        };
        var store = Store();
        await using var factory = EnabledFactory(store);
        var body = $$"""{"name":"{{name}}","description":"The one that triages.","graph":{{graph}}}""";

        using var created = await SendAsync(factory, "POST", Definitions, body);
        using var imported = await SendAsync(factory, "POST", Import, """{"format":"xe-graph-workflow","formatVersion":1,""" + body[1..]);
        var createdBody = await created.Content.ReadAsStringAsync();
        var importedBody = await imported.Content.ReadAsStringAsync();

        AssertEx.Equal(HttpStatusCode.BadRequest, created.StatusCode, createdBody);
        AssertEx.Equal(HttpStatusCode.BadRequest, imported.StatusCode, importedBody);
        AssertEx.Equal(ErrorList(createdBody), ErrorList(importedBody), "the import answers create's errors, keyed the same way.");
        await store.DidNotReceive().CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Import_WithABodyOverTheCap_Returns413AndNeverReachesTheStore()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);
        var graph = GraphWorkflowGraphs.StartAgentEnd.Replace("Analyze the input.", new string('a', (int)GraphWorkflowRequestSizeLimit.MaxBytes), StringComparison.Ordinal);

        using var response = await SendAsync(factory, "POST", Import, ImportBody(graph));

        AssertEx.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await RequestBodyTooLargeAssert.DeclaredProblemShapeAsync(response, $"POST {Import}");
        AssertEx.Empty(store.ReceivedCalls());
    }

    [Test]
    [Arguments("Übersicht für Märkte", "ubersicht-fur-markte.graph-workflow.json")]
    [Arguments("  Daily -- Report!  ", "daily-report.graph-workflow.json")]
    [Arguments("日本語", "graph-workflow.graph-workflow.json")]
    public void FileName_IsAnAsciiSlugOfTheName(string name, string expected) =>
        AssertEx.Equal(expected, GraphWorkflowShareFile.FileName(name));

    private static string ErrorList(string body)
    {
        using var document = JsonDocument.Parse(body);
        return string.Join("\n",
            document.RootElement.GetProperty("errors")
                    .EnumerateArray()
                    .Select(static error => $"{error.GetProperty("name").GetString()}: {error.GetProperty("reason").GetString()}")
                    .Order(StringComparer.Ordinal));
    }

    private static string ImportBody(string graph) =>
        $$"""{"format":"xe-graph-workflow","formatVersion":1,"name":"Triage","description":"The one that triages.","graph":{{graph}}}""";

    private static HttpRequestMessage Request(string method, string route, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string method, string route, string? body = null)
    {
        using var client = factory.CreateClient();
        using var request = Request(method, route, body);
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    private static IGraphWorkflowStore Store()
    {
        var store = Substitute.For<IGraphWorkflowStore>();
        store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Snapshot("Triage", GraphWorkflowGraphs.StartAgentEnd));
        store.CreateDefinitionAsync(Arg.Any<CreateGraphWorkflowDefinitionCommand>(), Arg.Any<CancellationToken>()).Returns(Snapshot("Triage", GraphWorkflowGraphs.StartAgentEnd));
        return store;
    }

    private static GraphWorkflowDefinitionSnapshot Snapshot(string name, string graphJson, Guid? id = null, bool allowsActions = false,
        GraphWorkflowTriggerSource triggerSources = GraphWorkflowTriggerSource.Manual | GraphWorkflowTriggerSource.Chat) =>
        new()
        {
            Id = id ?? DefinitionId,
            Name = name,
            Description = "The one that triages.",
            GraphJson = graphJson,
            GraphHash = "graph-hash",
            NodeCount = 3,
            SchemaVersion = 1,
            Kind = GraphWorkflowDefinitionKind.Standard,
            AllowsActions = allowsActions,
            TriggerSources = triggerSources,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 2
        };

    private static TestServerWebAppFactory EnabledFactory(IGraphWorkflowStore store) =>
        new()
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "true"
            },
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IGraphWorkflowStore>();
                services.AddSingleton(store);
            }
        };
}
