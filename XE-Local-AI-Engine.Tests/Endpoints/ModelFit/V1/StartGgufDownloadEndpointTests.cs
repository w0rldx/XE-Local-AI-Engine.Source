namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     HTTP-layer coverage for <c>POST model-fit/download</c>
///     (<see cref="XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.StartGgufDownloadEndpoint" />): the synchronous
///     failures <see cref="IGgufDownloadCoordinator.StartAsync" /> can surface must reach the operator as a classified
///     status code, not a 500. <see cref="GgufAcquisitionConflictException" /> is a 409 and a Hugging Face
///     <see cref="HuggingFaceDownloadFailure.NotFound" /> is a 404 — both mapped through the shared
///     <c>GgufDownloadEndpointSupport</c> the two knowledge-base recommended-download endpoints also use.
/// </summary>
public sealed class StartGgufDownloadEndpointTests
{
    private const string DownloadRoute = "/api/local/v1/model-fit/download";

    [Test]
    public async Task StartDownload_WhenTheModelNameIsAlreadyClaimed_IsConflict()
    {
        await using var factory = CreateFactory(new ThrowingDownloadCoordinator(new GgufAcquisitionConflictException()));
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("The model name or destination is already in use.", await ProblemFieldAsync(response, "title").ConfigureAwait(false));
    }

    [Test]
    public async Task StartDownload_WhenTheRepositoryOrFileIsMissing_IsNotFound()
    {
        var failure = new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound, "The requested GGUF file is not in that repository.");
        await using var factory = CreateFactory(new ThrowingDownloadCoordinator(failure));
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // The sanitized provider message is carried through as the detail so the operator learns what was missing.
        AssertEx.Equal("The requested GGUF file is not in that repository.", await ProblemFieldAsync(response, "detail").ConfigureAwait(false));
    }

    [Test]
    public async Task StartDownload_WhenTheRepositoryIsGated_IsForbidden()
    {
        var failure = new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Gated, "That repository is gated and no access token is configured.");
        await using var factory = CreateFactory(new ThrowingDownloadCoordinator(failure));
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Test]
    public async Task StartDownload_WhenTheCoordinatorFailsUnexpectedly_StaysAServerError()
    {
        // Anything the mapper does not classify must keep falling through to the global handler rather than being
        // dressed up as a client error — the mapper is deliberately narrow.
        await using var factory = CreateFactory(new ThrowingDownloadCoordinator(new IOException("the volume disappeared")));
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadRoute)
        {
            Content = JsonContent.Create(new
            {
                repoId = "bartowski/Qwen2.5-0.5B-Instruct-GGUF",
                quant = "Q4_K_M"
            })
        };
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<string?> ProblemFieldAsync(HttpResponseMessage response, string field)
    {
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(field).GetString();
    }

    private static TestServerWebAppFactory CreateFactory(IGgufDownloadCoordinator coordinator)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                // Lifetime mirrors production (singleton) so no captive dependency is introduced by the override.
                services.RemoveAll<IGgufDownloadCoordinator>();
                services.AddSingleton(coordinator);
            }
        };
    }

    private sealed class ThrowingDownloadCoordinator(Exception failure) : IGgufDownloadCoordinator
    {
        public Task<GgufDownloadTicket> StartAsync(GgufModelRequest request, CancellationToken ct)
        {
            return Task.FromException<GgufDownloadTicket>(failure);
        }

        public bool Cancel(string modelName)
        {
            return false;
        }

        public GgufDownloadStatus? GetStatus(string modelName)
        {
            return null;
        }

        public IReadOnlyList<GgufDownloadStatus> ListStatuses()
        {
            return [];
        }
    }
}
