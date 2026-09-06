namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using System.Text.Json;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Tool node picker's feed, over the wired host. The service behind it is the real one — its envelope is
///     pinned at its own definition site by <c>ToolInvocationServiceTests</c> — so what this class asserts is the
///     endpoint's own contribution: the operator gate, the disabled-node 404, and that the wire shape carries the
///     service's set through unfiltered and unwidened.
/// </summary>
public sealed class GraphWorkflowToolsEndpointTests
{
    private const string Tools = "/api/local/v1/graph-workflows/tools";

    /// <summary>
    ///     The eight tools that pass the D6 envelope at this tip, deliberately duplicated from
    ///     <c>ToolInvocationServiceTests</c> rather than shared: the point of asserting the set HERE is that the route
    ///     hands the envelope over whole, and a shared constant would move with any widening instead of failing on it.
    /// </summary>
    private static readonly string[] ExpectedInvocable =
    [
        "Calculate",
        "GetCurrentTime",
        "list_files",
        "read_document",
        "read_file",
        "read_surrounding_chunks",
        "search_knowledge_base",
        "search_text"
    ];

    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    public async Task ListTools_WithoutAToken_ReturnsUnauthorized()
    {
        using var client = Host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Tools);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "the tool list must require the operator token.");
    }

    [Test]
    public async Task ListTools_WithANonOperatorToken_ReturnsForbidden()
    {
        using var client = Host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Tools);
        Host.Factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode, "the tool list is operator-only, so an authenticated non-operator is refused.");
    }

    [Test]
    public async Task ListTools_WhenTheFeatureIsDisabled_ReturnsNotFound()
    {
        // A private host: the shared fixture exists to have the feature ON, and the gate is a request-path middleware
        // decided from configuration, so it can only be exercised by a host built with the switch off.
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            }
        };

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Tools);
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "a disabled node must answer 404 for the whole family, never 500.");
    }

    [Test]
    public async Task ListTools_ReturnsExactlyTheInvocableSet_WithAParseableSchemaEach()
    {
        using var response = await SendAsync().ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, body);
        using var document = JsonDocument.Parse(body);
        var tools = document.RootElement.GetProperty("tools").EnumerateArray().ToList();

        AssertEx.Equal(string.Join(',', ExpectedInvocable),
            string.Join(',', tools.Select(static tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal)),
            "the picker offers the D6 envelope and nothing else — no MCP tool, no custom tool, no write-class built-in.");

        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();
            AssertEx.NotNullOrEmpty(tool.GetProperty("description").GetString(), $"{name} must carry a description for the picker.");

            // The raw schema TEXT, not a nested object: S3 parses this string to draw the argument form, so what has
            // to hold is that the string it receives is itself a JSON object.
            var schema = tool.GetProperty("parameterSchema").GetString();
            AssertEx.NotNullOrEmpty(schema, $"{name} must carry the schema the runtime validates its arguments against.");
            using var parsed = JsonDocument.Parse(schema!);
            AssertEx.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind, $"{name}'s parameterSchema must parse as a JSON schema object.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync()
    {
        using var client = Host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Tools);
        Host.Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
