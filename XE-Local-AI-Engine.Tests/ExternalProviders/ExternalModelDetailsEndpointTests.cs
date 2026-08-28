namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The model-details endpoint's external branch: declarations, not a probe.
/// </summary>
/// <remarks>
///     Details are a local-runtime concept, so before this branch an <c>ext:</c> id would have been routed to the
///     default provider and probed against Ollama's <c>/api/show</c> — a 500 for a model the local runtime has never
///     seen, which is the same failure the Codex and Azure branches were added to prevent. The one detail an external
///     model genuinely has is the window its operator declared, and the chat context meter reads it.
/// </remarks>
public sealed class ExternalModelDetailsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetLocalModelDetails_ForARegisteredExternalModel_ReturnsTheDeclaredWindow()
    {
        var trust = new FakeModelTrustResolver().Register("local-box", "qwen3", contextLength: 65536);
        await using var factory = CreateFactory(trust);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, "/api/local/v1/models/ext%3Alocal-box%2Fqwen3/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var details = JsonSerializer.Deserialize<LocalModelDetailsResponse>(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false), JsonOptions)!;

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("ext:local-box/qwen3", details.ModelName);
        // Both fields: for an endpoint the node does not launch, the advertised ceiling and the effective window are
        // the same number, and the meter sizes against the effective one.
        AssertEx.Equal(expected: 65536, details.MaxContextTokens);
        AssertEx.Equal(expected: 65536, details.EffectiveContextTokens);
        // Ollama Modelfile concepts a remote endpoint has no equivalent of.
        AssertEx.Null(details.Template);
        AssertEx.Null(details.License);
    }

    [Test]
    public async Task GetLocalModelDetails_ForAnExternalModelWithNoDeclaredWindow_ReturnsNullRatherThanAGuess()
    {
        var trust = new FakeModelTrustResolver().Register("local-box", "qwen3");
        await using var factory = CreateFactory(trust);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, "/api/local/v1/models/ext%3Alocal-box%2Fqwen3/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var details = JsonSerializer.Deserialize<LocalModelDetailsResponse>(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false), JsonOptions)!;

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Null(details.MaxContextTokens);
    }

    [Test]
    public async Task GetLocalModelDetails_ForAnUnregisteredExternalModel_Returns404()
    {
        await using var factory = CreateFactory(new FakeModelTrustResolver());
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, "/api/local/v1/models/ext%3Agone%2Fqwen3/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        // A stale selection is a clean 404, exactly like a GGUF whose map row outlived its file.
        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static TestServerWebAppFactory CreateFactory(IModelTrustResolver trust)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IModelTrustResolver>();
                services.AddSingleton(trust);
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }
}
