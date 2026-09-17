namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using System.Net;
using System.Runtime.CompilerServices;
using NSubstitute;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Tests the provider eject path (<c>OllamaModelService.UnloadModelAsync</c>, behind the
///     <c>models/{modelName}/unload</c> endpoint) against the fake Ollama. The eviction must target the REQUESTED model,
///     not the shared client's <c>SelectedModel</c>.
/// </summary>
[Category(TestCategories.Integration)]
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

    [Test]
    public async Task UnloadModelAsync_WhenOllamaHasNeverHeardOfTheModel_IsIdempotentNoOp()
    {
        // Ollama answers /api/generate for an UNKNOWN model with 404 "model '<name>' not found, try pulling it first".
        // That reaches the caller as a bare HttpRequestException carrying the status, NOT an OllamaException: OllamaSharp
        // 5.4.30's EnsureSuccessStatusCodeAsync parses the body only for HTTP 400 and lets every other status fall
        // through to HttpResponseMessage.EnsureSuccessStatusCode(). Eject is documented idempotent on both surfaces that
        // share OllamaModelUnloader, and a model this runtime does not know is already in the requested state, so the
        // call must complete. The fake answers for any name and cannot produce a 404, hence the substituted client.
        var client = Substitute.For<IOllamaApiClient>();
        client.GenerateAsync(Arg.Any<GenerateRequest>(), Arg.Any<CancellationToken>())
              .Returns(FailingStream(new HttpRequestException("Not Found", inner: null, HttpStatusCode.NotFound)));
        using var service = new OllamaModelService(client);

        await service.UnloadModelAsync("ghost:latest").ConfigureAwait(false);

        _ = client.Received(1).GenerateAsync(Arg.Is<GenerateRequest>(request => request.Model == "ghost:latest"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModelAsync_WhenOllamaAnswersWithAServerError_Propagates()
    {
        // The 404 absorption is keyed on that ONE status, so a daemon that answers 500 is still a real fault. Absorbing
        // it would report a freed model while Ollama kept holding it.
        var client = Substitute.For<IOllamaApiClient>();
        client.GenerateAsync(Arg.Any<GenerateRequest>(), Arg.Any<CancellationToken>())
              .Returns(FailingStream(new HttpRequestException("Server Error", inner: null, HttpStatusCode.InternalServerError)));
        using var service = new OllamaModelService(client);

        var thrown = await AssertEx.ThrowsAsync<HttpRequestException>(() => service.UnloadModelAsync("qwen3:8b")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.InternalServerError, thrown.StatusCode);
    }

    [Test]
    public async Task UnloadModelAsync_WhenOllamaRejectsTheRequest_Propagates()
    {
        // HTTP 400 is the one status OllamaSharp parses into an OllamaException. It is a genuine rejection, not an
        // already-satisfied eject, so it must reach the caller rather than be mistaken for the not-found case.
        var client = Substitute.For<IOllamaApiClient>();
        client.GenerateAsync(Arg.Any<GenerateRequest>(), Arg.Any<CancellationToken>())
              .Returns(FailingStream(new OllamaException("model is currently loading")));
        using var service = new OllamaModelService(client);

        await AssertEx.ThrowsAsync<OllamaException>(() => service.UnloadModelAsync("qwen3:8b")).ConfigureAwait(false);
    }

    [Test]
    public async Task IsLoopbackModelInstalledAsync_WhenLoopbackDaemonHasTheModel_ReturnsTrue()
    {
        // The drafting eligibility check: a model is draftable only when a LOOPBACK daemon reports it installed.
        var client = LoopbackClientWith("qwen3:8b", "llama3.2:3b");
        using var service = new OllamaModelService(client);

        AssertEx.True(await service.IsLoopbackModelInstalledAsync("QWEN3:8B").ConfigureAwait(false),
            "the name match is case-insensitive, matching the classification and picker surfaces");
    }

    [Test]
    public async Task IsLoopbackModelInstalledAsync_WhenTheDaemonOnlyFillsTheModelField_ReturnsTrue()
    {
        // /api/tags fills "model" on current daemons and only "name" on older ones, which is why every name-keyed
        // surface reads it through OllamaModelName.ReadModelName. Comparing Model.Name alone made every model on a
        // current daemon look uninstalled, silently disqualifying it for drafting.
        var client = Substitute.For<IOllamaApiClient>();
        client.Uri.Returns(new Uri("http://127.0.0.1:11434"));
        client.ListLocalModelsAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IEnumerable<Model>>([
                  new Model
                  {
                      ModelName = "qwen3:8b"
                  }
              ]));
        using var service = new OllamaModelService(client);

        AssertEx.True(await service.IsLoopbackModelInstalledAsync("qwen3:8b").ConfigureAwait(false),
            "a daemon that reports the name in \"model\" must still count as installed");
    }

    [Test]
    public async Task IsLoopbackModelInstalledAsync_WhenLoopbackDaemonDoesNotHaveTheModel_ReturnsFalse()
    {
        var client = LoopbackClientWith("llama3.2:3b");
        using var service = new OllamaModelService(client);

        AssertEx.False(await service.IsLoopbackModelInstalledAsync("qwen3:8b").ConfigureAwait(false));
    }

    [Test]
    public async Task IsLoopbackModelInstalledAsync_WhenEndpointIsRemote_ReturnsFalseWithoutListing()
    {
        // Uri.IsLoopback is the same fact the composition-time SSRF guard enforces, read without throwing: a remote
        // endpoint makes every Ollama model ineligible, and the inventory is never even requested.
        var client = Substitute.For<IOllamaApiClient>();
        client.Uri.Returns(new Uri("http://198.51.100.7:11434"));
        using var service = new OllamaModelService(client);

        AssertEx.False(await service.IsLoopbackModelInstalledAsync("qwen3:8b").ConfigureAwait(false));

        _ = client.DidNotReceive().ListLocalModelsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsLoopbackModelInstalledAsync_WhenDaemonIsUnreachable_ReturnsFalse()
    {
        // An unreachable daemon is not an error here — it just means no Ollama model is installed as far as we know.
        var client = Substitute.For<IOllamaApiClient>();
        client.Uri.Returns(new Uri("http://127.0.0.1:11434"));
        client.ListLocalModelsAsync(Arg.Any<CancellationToken>())
              .Returns<Task<IEnumerable<Model>>>(_ => throw new HttpRequestException("Connection refused"));
        using var service = new OllamaModelService(client);

        AssertEx.False(await service.IsLoopbackModelInstalledAsync("qwen3:8b").ConfigureAwait(false));
    }

    private static IOllamaApiClient LoopbackClientWith(params string[] installedModelNames)
    {
        var client = Substitute.For<IOllamaApiClient>();
        client.Uri.Returns(new Uri("http://127.0.0.1:11434"));
        client.ListLocalModelsAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IEnumerable<Model>>([
                  .. installedModelNames.Select(static name => new Model
                  {
                      Name = name
                  })
              ]));
        return client;
    }

    /// <summary>An <c>/api/generate</c> stream that fails on first move, the way a non-200 response surfaces.</summary>
    private static async IAsyncEnumerable<GenerateResponseStream?> FailingStream(Exception failure,
        [EnumeratorCancellation]
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        throw failure;
#pragma warning disable CS0162 // Unreachable: the compiler needs a yield to make this an iterator.
        yield break;
#pragma warning restore CS0162
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
