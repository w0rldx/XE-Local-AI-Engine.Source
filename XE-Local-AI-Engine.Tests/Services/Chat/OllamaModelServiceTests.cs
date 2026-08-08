namespace XE_Local_AI_Engine.Tests.Services.Chat;

using OllamaSharp;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Tests the app-service eject path (<c>OllamaModelService.UnloadModelAsync</c>, behind the
///     <c>models/{modelName}/unload</c> endpoint) against the fake Ollama. The eviction must target the REQUESTED model,
///     not the shared client's <c>SelectedModel</c>.
/// </summary>
public sealed class OllamaModelServiceTests
{
    [Test]
    public async Task UnloadModelAsync_WhenInvoked_PostsGenerateWithRequestedModelToEvict()
    {
        // The shared client's SelectedModel is "chat"; the eject targets "qwen3:8b". OllamaSharp's
        // RequestModelUnloadAsync extension recorded "chat" here (it uses client.SelectedModel), which never freed the
        // requested model. The fix sends the REQUESTED model to /api/generate with keep_alive=0 so Ollama evicts it.
        await using var context = await CreateContextAsync("chat", "qwen3:8b").ConfigureAwait(false);

        await context.Service.UnloadModelAsync("qwen3:8b").ConfigureAwait(false);

        AssertEx.ContainsSingle(context.Server.RecordedRequests,
            request => request.Path == "/api/generate" && request.ModelName == "qwen3:8b");
    }

    [Test]
    public async Task UnloadModelAsync_WhenModelNotLoaded_IsIdempotentNoOp()
    {
        // The fake answers /api/generate for any model name, mirroring Ollama treating an unload of a not-held model as a
        // harmless no-op, so the eject action stays safe to retry.
        await using var context = await CreateContextAsync("chat").ConfigureAwait(false);

        await context.Service.UnloadModelAsync("not-loaded:latest").ConfigureAwait(false);

        AssertEx.ContainsSingle(context.Server.RecordedRequests,
            request => request.Path == "/api/generate" && request.ModelName == "not-loaded:latest");
    }

    private static async Task<ServiceTestContext> CreateContextAsync(params string[] models)
    {
        var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = models.Length > 0 ? models : ["chat"]
        }, CancellationToken.None).ConfigureAwait(false);

        var ollamaClient = new OllamaApiClient(server.BaseAddress)
        {
            // Mirror production: the shared client carries a fixed configured model, distinct from the ejected one.
            SelectedModel = "chat"
        };
        var service = new OllamaModelService(ollamaClient);
        return new ServiceTestContext(server, ollamaClient, service);
    }

    private sealed class ServiceTestContext : IAsyncDisposable
    {
        public ServiceTestContext(FakeOllamaServer server, OllamaApiClient ollamaClient, OllamaModelService service)
        {
            Server = server;
            OllamaClient = ollamaClient;
            Service = service;
        }

        public FakeOllamaServer Server { get; }

        public OllamaApiClient OllamaClient { get; }

        public OllamaModelService Service { get; }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            OllamaClient.Dispose();
            await Server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
