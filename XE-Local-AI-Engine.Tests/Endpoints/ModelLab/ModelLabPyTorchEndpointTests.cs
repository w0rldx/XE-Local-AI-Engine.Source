namespace XE_Local_AI_Engine.Tests.Endpoints.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The three PyTorch source routes: operator only, and a malformed request is a 400 before the service sees it.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabPyTorchEndpointTests
{
    private const string IndexRoute = "/api/local/v1/model-lab/architecture/org%2FTiny-GGUF%3AQ4_K_M/pytorch";
    private const string SourceRoute = "/api/local/v1/model-lab/pytorch-source?index=abc&file=activations.py";

    [Test]
    [Arguments("build")]
    [Arguments("index")]
    [Arguments("source")]
    public async Task Anonymous_Is401(string route)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = Request(route);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    [Arguments("build")]
    [Arguments("index")]
    [Arguments("source")]
    public async Task ANonOperator_Is403(string route)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = Request(route);
        factory.AddNonOperatorBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    [Arguments("""{"source":"somewhere"}""")]
    [Arguments("""{"source":"checkpoint"}""")]
    [Arguments("""{"source":"checkpoint","baseArtifactId":"00000000-0000-0000-0000-000000000000"}""")]
    [Arguments("""{"source":"huggingface"}""")]
    [Arguments("""{"source":"huggingface","repoId":"not-a-repo"}""")]
    public async Task AMalformedBuild_Is400(string body)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, IndexRoute)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    [Arguments("/api/local/v1/model-lab/pytorch-source?index=abc")]
    [Arguments("/api/local/v1/model-lab/pytorch-source?file=activations.py")]
    public async Task ASourceReadWithoutBothNames_Is400(string route)
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpRequestMessage Request(string route) =>
        route switch
        {
            "build" => new HttpRequestMessage(HttpMethod.Post, IndexRoute)
            {
                Content = JsonContent.Create(new
                {
                    source = "huggingface",
                    repoId = "Qwen/Qwen3.5-0.8B"
                })
            },
            "index" => new HttpRequestMessage(HttpMethod.Get, IndexRoute),
            _ => new HttpRequestMessage(HttpMethod.Get, SourceRoute)
        };
}
