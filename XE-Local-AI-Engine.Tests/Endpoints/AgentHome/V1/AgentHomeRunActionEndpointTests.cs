namespace XE_Local_AI_Engine.Tests.Endpoints.AgentHome.V1;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three per-run routes with their services substituted: what each route owes is the STATUS-CODE contract, the
///     operator gate, and the run id reaching the service unchanged — never a path, never a reason a probe could map
///     the disk with.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AgentHomeRunActionEndpointTests
{
    private const string RunId = "run-1758300000000-1";

    private static string DeleteRoute => $"/api/local/v1/agent-home/runs/{RunId}";

    private static string LogRoute => $"/api/local/v1/agent-home/runs/{RunId}/log";

    private static string PatchRoute => $"/api/local/v1/agent-home/runs/{RunId}/patch";

    [Test]
    [Arguments("DELETE", "/api/local/v1/agent-home/runs/run-1758300000000-1")]
    [Arguments("GET", "/api/local/v1/agent-home/runs/run-1758300000000-1/log")]
    [Arguments("GET", "/api/local/v1/agent-home/runs/run-1758300000000-1/patch")]
    public async Task PerRunRoutes_WithoutTheOperatorToken_AreUnauthorized(string method, string route)
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode,
            "a run's log, its patch and its removal all name what the agent did on the operator's own machine.");
    }

    [Test]
    [Arguments("DELETE", "/api/local/v1/agent-home/runs/run-1758300000000-1")]
    [Arguments("GET", "/api/local/v1/agent-home/runs/run-1758300000000-1/log")]
    [Arguments("GET", "/api/local/v1/agent-home/runs/run-1758300000000-1/patch")]
    public async Task PerRunRoutes_WithANonOperatorToken_AreForbidden(string method, string route)
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        factory.AddNonOperatorBearerToken(request);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode,
            "an MCP or proxy principal is a strictly lesser one and must never reach a run's contents.");
    }

    [Test]
    public async Task Delete_WhenTheServiceRemovedTheRun_Answers204AndPassesTheIdThrough()
    {
        var deletes = new StubDeleteService { Outcome = AgentHomeRunDeleteOutcome.Deleted };

        using var response = await SendAsync(HttpMethod.Delete, DeleteRoute, deletes: deletes);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertEx.Equal(RunId, deletes.LastRunId);
    }

    [Test]
    public async Task Delete_ForAnUnknownRun_Answers404WithoutNamingAPath()
    {
        using var response = await SendAsync(HttpMethod.Delete, DeleteRoute,
            deletes: new StubDeleteService { Outcome = AgentHomeRunDeleteOutcome.NotFound });

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertEx.False((await response.Content.ReadAsStringAsync()).Contains("agent-home", StringComparison.OrdinalIgnoreCase),
            "a 404 that names where it looked lets a caller map the disk by probing.");
    }

    [Test]
    public async Task Delete_WhileARunIsInFlight_Answers409WithSomethingTheOperatorCanRead()
    {
        using var response = await SendAsync(HttpMethod.Delete, DeleteRoute,
            deletes: new StubDeleteService { Outcome = AgentHomeRunDeleteOutcome.Conflict });

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.NotNullOrEmpty(await response.Content.ReadAsStringAsync(),
            "a refusal the UI cannot show the operator is a refusal they cannot act on.");
    }

    [Test]
    [Arguments("run id with spaces")]
    [Arguments("run-1;rm -rf")]
    [Arguments("run.1")]
    public async Task Delete_WithAMalformedRunId_Answers400WithoutReachingTheService(string runId)
    {
        var deletes = new StubDeleteService();

        using var response = await SendAsync(HttpMethod.Delete,
            $"/api/local/v1/agent-home/runs/{Uri.EscapeDataString(runId)}",
            deletes: deletes);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, deletes.Calls, "a malformed id is refused at the edge, before any filesystem call.");
    }

    [Test]
    public async Task Log_Answers200WithTheTextAndTheTruncationFlag()
    {
        var runs = new StubRunListService { Log = new AgentHomeRunText { Text = "{\"eventName\":\"started\"}", Truncated = true } };

        using var document = await ReadJsonAsync(LogRoute, runs);

        AssertEx.Equal("{\"eventName\":\"started\"}", document.RootElement.GetProperty("text").GetString());
        AssertEx.True(document.RootElement.GetProperty("truncated").GetBoolean());
        AssertEx.Equal(RunId, runs.LastLogRunId);
    }

    [Test]
    public async Task Log_ForARunTheNodeWillNotServe_Answers404()
    {
        using var response = await SendAsync(HttpMethod.Get, LogRoute, runs: new StubRunListService { Log = null });

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Log_ForARunWithNoLog_Answers200WithEmptyText()
    {
        var runs = new StubRunListService { Log = new AgentHomeRunText { Text = string.Empty, Truncated = false } };

        using var document = await ReadJsonAsync(LogRoute, runs);

        AssertEx.Equal(string.Empty, document.RootElement.GetProperty("text").GetString(),
            "\"this run left nothing here\" is not the same answer as \"no such run\".");
    }

    [Test]
    public async Task Patch_Answers200WithTheRawDiffTextUnaltered()
    {
        // Control and bidi characters are model-authored and must survive the wire unchanged: the viewer flags them,
        // the contract does not rewrite them into a document the run never produced.
        const string Diff = "diff --git a/x b/x\n+‮evil\u0007\n";
        var runs = new StubRunListService { Patch = new AgentHomeRunText { Text = Diff, Truncated = false } };

        using var document = await ReadJsonAsync(PatchRoute, runs);

        AssertEx.Equal(Diff, document.RootElement.GetProperty("text").GetString());
        AssertEx.False(document.RootElement.GetProperty("truncated").GetBoolean());
        AssertEx.Equal(RunId, runs.LastPatchRunId);
    }

    [Test]
    public async Task Patch_ForARunTheNodeWillNotServe_Answers404()
    {
        using var response = await SendAsync(HttpMethod.Get, PatchRoute, runs: new StubRunListService { Patch = null });

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonDocument> ReadJsonAsync(string route, StubRunListService runs)
    {
        using var response = await SendAsync(HttpMethod.Get, route, runs: runs);
        var payload = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"GET {route} answered {(int)response.StatusCode}: {payload}");
        return JsonDocument.Parse(payload);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method,
        string route,
        StubRunListService? runs = null,
        StubDeleteService? deletes = null)
    {
        await using var factory = NewFactory(runs, deletes);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);

        return await client.SendAsync(request);
    }

    private static TestServerWebAppFactory NewFactory(StubRunListService? runs = null, StubDeleteService? deletes = null) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IAgentHomeRunListService>();
                services.AddSingleton<IAgentHomeRunListService>(runs ?? new StubRunListService());
                services.RemoveAll<IAgentHomeRunDeleteService>();
                services.AddSingleton<IAgentHomeRunDeleteService>(deletes ?? new StubDeleteService());
            }
        };

    private sealed class StubDeleteService : IAgentHomeRunDeleteService
    {
        public AgentHomeRunDeleteOutcome Outcome { get; init; } = AgentHomeRunDeleteOutcome.Deleted;

        public int Calls { get; private set; }

        public string? LastRunId { get; private set; }

        public Task<AgentHomeRunDeleteOutcome> DeleteAsync(string runId, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRunId = runId;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class StubRunListService : IAgentHomeRunListService
    {
        public AgentHomeRunText? Log { get; init; }

        public AgentHomeRunText? Patch { get; init; }

        public string? LastLogRunId { get; private set; }

        public string? LastPatchRunId { get; private set; }

        public Task<AgentHomeRunPage> ListAsync(int limit, int offset, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentHomeRunPage { Items = [], TotalCount = 0 });

        public Task<AgentHomeRunText?> ReadLogAsync(string runId, CancellationToken cancellationToken = default)
        {
            LastLogRunId = runId;
            return Task.FromResult(Log);
        }

        public Task<AgentHomeRunText?> ReadPatchAsync(string runId, CancellationToken cancellationToken = default)
        {
            LastPatchRunId = runId;
            return Task.FromResult(Patch);
        }
    }
}
