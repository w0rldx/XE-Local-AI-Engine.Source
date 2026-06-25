namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using OllamaSharp;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies that the running-model snapshot (and its memory footprint) maps correctly from Ollama's <c>/api/ps</c>
///     payload through both surfaces that produce a <see cref="XE_Local_AI_Engine.Providers.Abstractions.RunningModelSnapshot" />:
///     the provider-neutral <see cref="OllamaModelCapabilityClient" /> and the app-service <see cref="OllamaModelService" />.
///     A graceful unload is exercised end-to-end against the fake runtime.
/// </summary>
public sealed class RunningModelSnapshotMappingTests
{
    [Test]
    public async Task CapabilityClient_ListRunningModels_MapsSizeAndVram()
    {
        await using var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = ["llama3:8b"]
        }, CancellationToken.None).ConfigureAwait(false);
        server.State.RunningModels =
        [
            new FakeOllamaState.FakeOllamaRunningModel("llama3:8b", DateTimeOffset.UtcNow.AddMinutes(5), SizeBytes: 5_000_000_000, SizeVramBytes: 4_000_000_000)
        ];
        using var ollamaClient = new OllamaApiClient(server.BaseAddress);
        var capabilityClient = new OllamaModelCapabilityClient(ollamaClient);

        var running = await capabilityClient.ListRunningModelsAsync(CancellationToken.None).ConfigureAwait(false);

        var snapshot = AssertEx.NotNull(running.SingleOrDefault());
        AssertEx.Equal(expected: 5_000_000_000L, snapshot.SizeBytes);
        AssertEx.Equal(expected: 4_000_000_000L, snapshot.SizeVramBytes);
        AssertEx.True(snapshot.ExpiresAt.HasValue);
    }

    [Test]
    public async Task ModelService_ListRunningModels_MapsSizeAndVram()
    {
        await using var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = ["llama3:8b"]
        }, CancellationToken.None).ConfigureAwait(false);
        server.State.RunningModels =
        [
            new FakeOllamaState.FakeOllamaRunningModel("llama3:8b", DateTimeOffset.UtcNow.AddMinutes(5), SizeBytes: 7_000_000_000, SizeVramBytes: 6_000_000_000)
        ];
        using var ollamaClient = new OllamaApiClient(server.BaseAddress);
        using var modelService = new OllamaModelService(ollamaClient);

        var running = await modelService.ListRunningModelsAsync(CancellationToken.None).ConfigureAwait(false);

        var snapshot = AssertEx.NotNull(running.SingleOrDefault());
        AssertEx.Equal("llama3:8b", snapshot.Name);
        AssertEx.Equal(expected: 7_000_000_000L, snapshot.SizeBytes);
        AssertEx.Equal(expected: 6_000_000_000L, snapshot.SizeVramBytes);
    }

    [Test]
    public async Task ModelService_ListRunningModels_WhenSizeUnreported_LeavesFootprintNull()
    {
        await using var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = ["llama3:8b"]
        }, CancellationToken.None).ConfigureAwait(false);
        // Zero size/size_vram models a runtime that does not report a footprint; the mapping must surface null rather than 0.
        // (A running model always reports an expiry, so this case isolates the size/vram normalization.)
        server.State.RunningModels =
        [
            new FakeOllamaState.FakeOllamaRunningModel("llama3:8b", DateTimeOffset.UtcNow.AddMinutes(5))
        ];
        using var ollamaClient = new OllamaApiClient(server.BaseAddress);
        using var modelService = new OllamaModelService(ollamaClient);

        var running = await modelService.ListRunningModelsAsync(CancellationToken.None).ConfigureAwait(false);

        var snapshot = AssertEx.NotNull(running.SingleOrDefault());
        AssertEx.Null(snapshot.SizeBytes);
        AssertEx.Null(snapshot.SizeVramBytes);
    }

    [Test]
    public void Mapper_ToRunningResponse_WhenExpiryAndFootprintMissing_LeavesFieldsNull()
    {
        // A snapshot with no expiry/footprint maps to a row with null memory + null countdown rather than zeroed values, so
        // the UI can omit those columns.
        var response = LocalModelsMapper.ToRunningResponse([
            new RunningModelSnapshot("llama3:8b", ModelName: null, ExpiresAt: null)
        ]);

        AssertEx.True(response.IsAvailable);
        var model = AssertEx.NotNull(response.Items.SingleOrDefault());
        AssertEx.Equal("llama3:8b", model.ModelName);
        AssertEx.Null(model.SizeBytes);
        AssertEx.Null(model.SizeVramBytes);
        AssertEx.Null(model.ExpiresAtUtc);
    }

    [Test]
    public void Mapper_ToRunningResponse_PrefersModelNameAndDropsNamelessEntries()
    {
        // The runtime may report the canonical id under "model"; nameless rows (neither field set) are dropped.
        var response = LocalModelsMapper.ToRunningResponse([
            new RunningModelSnapshot("raw", "llama3:8b", ExpiresAt: null),
            new RunningModelSnapshot(Name: null, ModelName: null, ExpiresAt: null)
        ]);

        var model = AssertEx.NotNull(response.Items.SingleOrDefault());
        AssertEx.Equal("llama3:8b", model.ModelName);
    }

    [Test]
    public async Task ModelService_UnloadModel_GracefullyRequestsRuntimeUnload()
    {
        await using var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = ["llama3:8b"]
        }, CancellationToken.None).ConfigureAwait(false);
        using var ollamaClient = new OllamaApiClient(server.BaseAddress);
        using var modelService = new OllamaModelService(ollamaClient);

        // keep_alive=0 is issued via an empty-prompt generate (POST /api/generate); unloading a model that is not "loaded"
        // in the fake runtime is still a no-op success (idempotent). The decoded model name reaching the service is covered
        // by the endpoint tests; here we prove the graceful unload route is exercised.
        await modelService.UnloadModelAsync("llama3:8b", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Contains(server.RecordedRequests, request => request.Method == "POST" && request.Path == "/api/generate");
    }
}
