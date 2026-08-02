namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint integration tests for the preview workflow API: every route requires the operator token (401 without
///     it), and executing an unsaved inline graph starts a run while persisting nothing.
/// </summary>
public sealed class PreviewWorkflowEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task PreviewEndpoints_RequireOperator()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        var unauthorized = new (HttpMethod Method, string Route)[]
        {
            (HttpMethod.Get, $"{ApiPrefix}/preview/workflows"),
            (HttpMethod.Post, $"{ApiPrefix}/preview/workflows"),
            (HttpMethod.Get, $"{ApiPrefix}/preview/workflows/{Guid.NewGuid()}"),
            (HttpMethod.Put, $"{ApiPrefix}/preview/workflows/{Guid.NewGuid()}"),
            (HttpMethod.Delete, $"{ApiPrefix}/preview/workflows/{Guid.NewGuid()}"),
            (HttpMethod.Post, $"{ApiPrefix}/preview/workflows/{Guid.NewGuid()}/execute"),
            (HttpMethod.Post, $"{ApiPrefix}/preview/runs/execute"),
            (HttpMethod.Get, $"{ApiPrefix}/preview/runs"),
            (HttpMethod.Get, $"{ApiPrefix}/preview/runs/{Guid.NewGuid()}"),
            (HttpMethod.Post, $"{ApiPrefix}/preview/runs/cancel-all"),
            (HttpMethod.Post, $"{ApiPrefix}/preview/runs/{Guid.NewGuid()}/continue"),
            (HttpMethod.Post, $"{ApiPrefix}/preview/runs/{Guid.NewGuid()}/cancel")
        };

        foreach (var (method, route) in unauthorized)
        {
            using var request = new HttpRequestMessage(method, route);
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
            {
                request.Content = JsonContent.Create(new
                {
                });
            }

            using var response = await client.SendAsync(request).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
        }
    }

    [Test]
    public async Task PreviewEndpoints_ExecuteUnsaved_RunsWithoutPersisting()
    {
        // Substitute the runner with a scripted session so the inline run never touches Ollama.
        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IPreviewWorkflowRunner>();
                services.AddSingleton<IPreviewWorkflowRunner>(new FakePreviewWorkflowRunner((_, _) =>
                    new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunCompleted("done")])));
            }
        };
        using var client = factory.CreateClient();

        var graph = PreviewGraphBuilder.Linear();

        using var executeRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/preview/runs/execute")
        {
            Content = JsonContent.Create(new
            {
                graph
            })
        };
        factory.AddNodeBearerToken(executeRequest);

        using var executeResponse = await client.SendAsync(executeRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        var started = await executeResponse.Content.ReadFromJsonAsync<PreviewRunStartedResponse>(JsonOptions).ConfigureAwait(false);
        AssertEx.NotEqual(Guid.Empty, started!.RunId);

        // Nothing persisted: the workflow library is still empty.
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/preview/workflows");
        factory.AddNodeBearerToken(listRequest);
        using var listResponse = await client.SendAsync(listRequest).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        var items = list.GetProperty("items");
        AssertEx.Equal(expected: 0, items.GetArrayLength());
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("continue")]
    public async Task PreviewRunControl_BodyLessPost_IsAcceptedNot415(string action)
    {
        // Regression for the live 415: these route-only POSTs bind the runId from the route, so a well-behaved client
        // sends no body — and therefore no Content-Type. The endpoints must accept that instead of answering 415
        // Unsupported Media Type. An unknown run yields 404 (authorized, body accepted, run simply not found), which
        // proves the request was bound and dispatched rather than rejected at the media-type gate.
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        // No HttpContent at all → the request carries no Content-Type header (the exact shape of a body-less fetch).
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/preview/runs/{Guid.NewGuid()}/{action}");
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, $"Body-less POST to {action} must not return 415.");
        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"An unknown run on body-less {action} must report 404 (authorized + bound), not 415.");
    }

    [Test]
    public async Task PreviewRuns_AreDiscoverableById_AndCancelAllReclaimsTheirSlots()
    {
        // The reload-leak recovery path end to end over HTTP: a run whose id is no longer held by any page must be
        // findable via GET preview/runs, fetchable by id, and clearable via cancel-all — none of which existed before.
        await using var factory = new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IPreviewWorkflowRunner>();
                // Parks on Pause: exactly the state that used to hold a concurrency slot forever.
                services.AddSingleton<IPreviewWorkflowRunner>(new FakePreviewWorkflowRunner((_, _) =>
                    new ScriptedPreviewRunSession([PreviewWorkflowUpdate.RunPaused("pause", "upstream", "req-1")])));
            }
        };
        using var client = factory.CreateClient();

        using var executeRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/preview/runs/execute")
        {
            Content = JsonContent.Create(new
            {
                graph = PreviewGraphBuilder.Linear()
            })
        };
        factory.AddNodeBearerToken(executeRequest);
        using var executeResponse = await client.SendAsync(executeRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        var started = await executeResponse.Content.ReadFromJsonAsync<PreviewRunStartedResponse>(JsonOptions).ConfigureAwait(false);
        var runId = started!.RunId;

        // Discoverable in the list — this is the call the client makes after a reload to find orphaned runs. The run is
        // in the registry before execute returns, so no polling is needed.
        var listed = await ListRunIds(client, factory).ConfigureAwait(false);
        AssertEx.Contains(listed, runId, "the started run must appear in GET preview/runs.");

        // Fetchable by id, and reaching Paused — the exact state a reloaded page used to be unable to reach.
        var run = await PollRunAsync(client, factory, runId,
                static r => string.Equals(r.GetProperty("state").GetString(), "Paused", StringComparison.Ordinal))
            .ConfigureAwait(false);
        AssertEx.Equal(runId.ToString(), run.GetProperty("runId").GetString());
        AssertEx.True(run.GetProperty("isLive").GetBoolean(), "a run holding a slot must report isLive.");
        AssertEx.Equal(expected: "pause", run.GetProperty("pausedNodeId").GetString());

        // An unknown id is a 404 so the client can drop a stale runId out of its route.
        using var missingRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/preview/runs/{Guid.NewGuid()}");
        factory.AddNodeBearerToken(missingRequest);
        using var missingResponse = await client.SendAsync(missingRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        // Cancel-all reclaims the slot without a node restart.
        using var cancelAllRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/preview/runs/cancel-all");
        factory.AddNodeBearerToken(cancelAllRequest);
        using var cancelAllResponse = await client.SendAsync(cancelAllRequest).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, cancelAllResponse.StatusCode);
        var cancelAll = await cancelAllResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, cancelAll.GetProperty("cancelledCount").GetInt32());

        var after = await PollRunAsync(client, factory, runId, static r => !r.GetProperty("isLive").GetBoolean())
            .ConfigureAwait(false);
        AssertEx.False(after.GetProperty("isLive").GetBoolean(), "a cancelled run must no longer hold a concurrency slot.");
        AssertEx.Equal(expected: "Cancelled", after.GetProperty("state").GetString());
    }

    private static async Task<List<Guid>> ListRunIds(HttpClient client, TestingWebAppFactory factory)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/preview/runs");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);

        return [.. payload.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("runId").GetGuid())];
    }

    /// <summary>GETs the run until <paramref name="predicate" /> holds (the run's state machine advances on a background drain).</summary>
    private static async Task<JsonElement> PollRunAsync(HttpClient client,
        TestingWebAppFactory factory,
        Guid runId,
        Func<JsonElement, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        JsonElement last = default;

        while (DateTime.UtcNow < deadline)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/preview/runs/{runId}");
            factory.AddNodeBearerToken(request);
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, "the run must stay resolvable by id while it is live or retained.");

            last = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"Run {runId} never reached the expected state. Last: {last}");
    }

    private sealed record PreviewRunStartedResponse(Guid RunId);
}
