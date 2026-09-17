namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The editor's probe: the same parser a save runs, asked without saving. Its answer is a REPORT, so it is a 200
///     whether the graph is clean or broken — the client needs one shape either way.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowValidateEndpointTests
{
    private const string Validate = "/api/local/v1/graph-workflows/definitions/validate";

    [Test]
    public async Task Validate_WithARoutableGraph_Answers200WithNoErrorsAndTheNodeCount()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, Body(GraphWorkflowGraphs.StartAgentEnd)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.True(document.RootElement.GetProperty("valid").GetBoolean(), $"StartAgentEnd routes, so the report is clean: {body}");
        AssertEx.Equal(0, document.RootElement.GetProperty("errors").GetArrayLength());
        AssertEx.Equal(3, document.RootElement.GetProperty("nodeCount").GetInt32());
        AssertEx.Equal(0, document.RootElement.GetProperty("warnings").GetArrayLength(), $"a graph with nothing to say about it says nothing: {body}");
    }

    /// <summary>
    ///     The pause-context warning on the wire. It is NOT an error: <c>valid</c> stays true, <c>errors</c> stays
    ///     empty, and a definition carrying it saves and starts — the report only tells the author that the node after
    ///     the pause will receive the decision rather than the answer.
    /// </summary>
    [Test]
    public async Task Validate_WithANodeReachedOnlyThroughAPause_Answers200ValidWithAWarning()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, Body(GraphWorkflowGraphs.PauseBetweenTwoAgents)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.True(document.RootElement.GetProperty("valid").GetBoolean(), $"a warning never makes a graph invalid: {body}");
        AssertEx.Equal(0, document.RootElement.GetProperty("errors").GetArrayLength());

        var warnings = document.RootElement.GetProperty("warnings").EnumerateArray().ToArray();
        AssertEx.Equal(1, warnings.Length, $"one warning, on the node that loses the content: {body}");
        AssertEx.Equal("summarize", warnings[0].GetProperty("key").GetString());
        AssertEx.Contains(warnings[0].GetProperty("message").GetString(), "Add an edge from 'analyze' to 'summarize'");
    }

    /// <summary>The cure, over the wire: the same graph with the context edge reports nothing at all.</summary>
    [Test]
    public async Task Validate_WithTheContextEdgeAroundThePause_Answers200WithNoWarning()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, Body(GraphWorkflowGraphs.PauseBetweenTwoAgentsWithContextEdge)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        AssertEx.True(document.RootElement.GetProperty("valid").GetBoolean());
        AssertEx.Equal(0, document.RootElement.GetProperty("warnings").GetArrayLength(), $"the context edge gives the node a non-Pause predecessor: {body}");
    }

    /// <summary>
    ///     The response-schema warning on the wire (S6-9). The schema an Agent node declares does not reach the grammar
    ///     as written — the adapter's strict transform drops the value keywords and makes every property required — so
    ///     the report says so while the graph stays valid, saveable and runnable.
    /// </summary>
    [Test]
    public async Task Validate_WithAResponseSchemaTheRuntimeRewrites_Answers200ValidWithAWarning()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, Body(GraphWorkflowGraphs.PauseAfterALooseResponseSchema)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.True(document.RootElement.GetProperty("valid").GetBoolean(), $"a schema warning never makes a graph invalid: {body}");
        AssertEx.Equal(0, document.RootElement.GetProperty("errors").GetArrayLength());

        var warnings = document.RootElement.GetProperty("warnings").EnumerateArray().ToArray();
        AssertEx.Equal("analyze, summarize",
            string.Join(", ", warnings.Select(static warning => warning.GetProperty("key").GetString() ?? string.Empty).Order(StringComparer.Ordinal)),
            $"the two warning kinds ride the same list, on their own nodes: {body}");
        var schemaWarning = warnings.Single(static warning => warning.GetProperty("key").GetString() == "analyze");
        AssertEx.Contains(schemaWarning.GetProperty("message").GetString(), "'pattern'");
        AssertEx.Contains(schemaWarning.GetProperty("message").GetString(), "requires every declared property");
    }

    [Test]
    public async Task Validate_WithAnUnroutableGraph_Answers200WithEveryError()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var response = await SendAsync(factory, Body(GraphWorkflowGraphs.TwoNodeConfigErrors)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "a graph that does not route is a report to draw, not a request that failed.");
        using var document = JsonDocument.Parse(body);
        AssertEx.False(document.RootElement.GetProperty("valid").GetBoolean());
        AssertEx.Equal(4, document.RootElement.GetProperty("nodeCount").GetInt32(), "the count comes off the authored document, so it is answerable for a graph the parser refused.");

        var errors = document.RootElement.GetProperty("errors").EnumerateArray().ToArray();
        AssertEx.Equal(2, errors.Length, $"both node failures are reported, not the first one: {body}");
        var keys = errors.Select(static error => error.GetProperty("key").GetString() ?? string.Empty).ToArray();
        AssertEx.Contains(keys, "a", $"each error names the element it belongs to, so the editor draws it on that node: {body}");
        AssertEx.Contains(keys, "b", $"each error names the element it belongs to, so the editor draws it on that node: {body}");
        AssertEx.Contains(errors.Select(static error => error.GetProperty("message").GetString() ?? string.Empty),
            static message => message.Contains("reasoningEffort", StringComparison.Ordinal));
    }

    /// <summary>The whole point of a probe: the editor may ask about a half-written canvas without leaving a row behind.</summary>
    [Test]
    public async Task Validate_PersistsNothing()
    {
        var store = Store();
        await using var factory = EnabledFactory(store);

        using var clean = await SendAsync(factory, Body(GraphWorkflowGraphs.StartAgentEnd)).ConfigureAwait(false);
        using var broken = await SendAsync(factory, Body(GraphWorkflowGraphs.TwoNodeConfigErrors)).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, clean.StatusCode);
        AssertEx.Equal(HttpStatusCode.OK, broken.StatusCode);
        AssertEx.Empty(store.ReceivedCalls());
    }

    private static string Body(string graph) =>
        $$"""{"graph":{{graph}}}""";

    private static IGraphWorkflowStore Store() =>
        Substitute.For<IGraphWorkflowStore>();

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string body)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Validate)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

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
