namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
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
            (HttpMethod.Post, $"{ApiPrefix}/preview/runs/{Guid.NewGuid()}/continue"),
            (HttpMethod.Post, $"{ApiPrefix}/preview/runs/{Guid.NewGuid()}/cancel")
        };

        foreach (var (method, route) in unauthorized)
        {
            using var request = new HttpRequestMessage(method, route);
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
            {
                request.Content = JsonContent.Create(new { });
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
            Content = JsonContent.Create(new { graph })
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
        AssertEx.Equal(0, items.GetArrayLength());
    }

    private sealed record PreviewRunStartedResponse(Guid RunId);
}
