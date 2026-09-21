namespace XE_Local_AI_Engine.Tests.Endpoints.AgentHome.V1;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The read-only run-history route, with the service substituted: what this route owes is the STATUS-CODE
///     contract, the operator gate, and the paging parameters reaching the service unchanged.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ListAgentHomeRunsEndpointTests
{
    private const string Route = "/api/local/v1/agent-home/runs";

    [Test]
    public async Task List_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized()
    {
        await using var factory = NewFactory(new StubRunListService());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(Route, UriKind.Relative));

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode,
            "the run history names what the operator's agent did on their own machine; it must require the operator token.");
    }

    [Test]
    public async Task List_WithANonOperatorToken_ReturnsForbidden()
    {
        await using var factory = NewFactory(new StubRunListService());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode,
            "an MCP or proxy principal is a strictly lesser one and must never reach the run history.");
    }

    [Test]
    public async Task List_WithNoParameters_Answers200WithTheDefaultPage()
    {
        var service = new StubRunListService
        {
            Page = new AgentHomeRunPage
            {
                TotalCount = 7,
                Items =
                [
                    new AgentHomeRunSummary
                    {
                        RunId = "run-1758300000000-1",
                        StartedAtUtc = DateTimeOffset.UnixEpoch,
                        Outcome = "Completed",
                        PatchExported = true,
                        ChangedFileCount = 3,
                        ApplyState = AgentHomeRunApplyStates.Applied,
                        ConversationId = Guid.Empty,
                        SizeBytes = 4096
                    }
                ]
            }
        };

        using var document = await SendAsync(service, Route, HttpStatusCode.OK);

        AssertEx.Equal(expected: 7, document.RootElement.GetProperty("totalCount").GetInt32());
        var item = document.RootElement.GetProperty("items")[0];
        AssertEx.Equal("run-1758300000000-1", item.GetProperty("runId").GetString());
        AssertEx.Equal("Completed", item.GetProperty("outcome").GetString());
        AssertEx.Equal("applied", item.GetProperty("applyState").GetString());
        AssertEx.True(item.GetProperty("patchExported").GetBoolean());
        AssertEx.Equal(expected: 3, item.GetProperty("changedFileCount").GetInt32());
        AssertEx.Equal(expected: 25, service.LastLimit, "a caller that names no page size gets the endpoint's default.");
        AssertEx.Equal(expected: 0, service.LastOffset);
    }

    [Test]
    public async Task List_WithAnEmptyHistory_Answers200WithAnEmptyPage()
    {
        using var document = await SendAsync(new StubRunListService(), Route, HttpStatusCode.OK);

        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("items").GetArrayLength(),
            "a node with no runs answers an empty page, never a 404.");
    }

    [Test]
    public async Task List_WithPagingParameters_PassesThemToTheService()
    {
        var service = new StubRunListService();

        using var document = await SendAsync(service, $"{Route}?limit=10&offset=20", HttpStatusCode.OK);

        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("totalCount").GetInt32());
        AssertEx.Equal(expected: 10, service.LastLimit);
        AssertEx.Equal(expected: 20, service.LastOffset);
    }

    [Test]
    [Arguments("?limit=0", "a page of no runs is not a page")]
    [Arguments("?limit=201", "a page past the ceiling would make one request read the whole history")]
    [Arguments("?offset=-1", "a negative offset is not a position in the list")]
    public async Task List_WithAnOutOfRangePageRequest_Answers400WithoutReachingTheService(string query, string why)
    {
        var service = new StubRunListService();
        await using var factory = NewFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route + query);
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, why);
        AssertEx.Equal(expected: 0, service.Calls, "a refused page request must not reach the service at all.");
    }

    private static async Task<JsonDocument> SendAsync(StubRunListService service, string route, HttpStatusCode expected)
    {
        await using var factory = NewFactory(service);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(expected, response.StatusCode, $"GET {route} answered {(int)response.StatusCode}: {payload}");
        return JsonDocument.Parse(payload);
    }

    private static TestServerWebAppFactory NewFactory(IAgentHomeRunListService service) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IAgentHomeRunListService>();
                services.AddSingleton(service);
            }
        };

    /// <summary>The service, stubbed to one page and recording the paging the endpoint composed.</summary>
    private sealed class StubRunListService : IAgentHomeRunListService
    {
        public AgentHomeRunPage Page { get; init; } = new() { Items = [], TotalCount = 0 };

        public int Calls { get; private set; }

        public int LastLimit { get; private set; }

        public int LastOffset { get; private set; }

        public Task<AgentHomeRunPage> ListAsync(int limit, int offset, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastLimit = limit;
            LastOffset = offset;
            return Task.FromResult(Page);
        }

        // The text reads are this stub's neighbours on the interface; their own route tests own them.
        public Task<AgentHomeRunText?> ReadLogAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentHomeRunText?>(null);

        public Task<AgentHomeRunText?> ReadPatchAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentHomeRunText?>(null);
    }
}
