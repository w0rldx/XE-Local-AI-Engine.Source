namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one-time FRR-2 upgrade backfill: every installed Ollama model that has no <c>model_provider_map</c> row is
///     mapped to <c>ollama</c> so it does not silently re-route to llama.cpp under the flipped default. Already-mapped
///     models are left untouched (no clobbering an operator override), and a failure to list the installed models
///     (Ollama absent/unreachable) is a no-op rather than a startup crash.
/// </summary>
public sealed class OllamaProviderMapBackfillTests
{
    [Test]
    public async Task UnmappedInstalledModels_AreMappedToOllama()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        using var provider = BuildProvider(new FakeOllamaModelService(["llama3.2:3b", "qwen3:0.6b"]), mapStore);

        await Backfill(provider);

        AssertEx.Equal(OllamaLocalModelProvider.OllamaProviderName, mapStore.Mappings["llama3.2:3b"].ProviderName);
        AssertEx.Equal(OllamaLocalModelProvider.OllamaProviderName, mapStore.Mappings["qwen3:0.6b"].ProviderName);
    }

    [Test]
    public async Task AlreadyMappedModel_IsLeftUntouched()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        // An operator (or a forward pull) already mapped this model to llamacpp — the backfill must not overwrite it.
        mapStore.Seed("custom:gguf", "llamacpp");
        mapStore.ResetMutationCount();
        using var provider = BuildProvider(new FakeOllamaModelService(["custom:gguf"]), mapStore);

        await Backfill(provider);

        AssertEx.Equal("llamacpp", mapStore.Mappings["custom:gguf"].ProviderName);
        AssertEx.Equal(expected: 0, mapStore.MutationCount);
    }

    [Test]
    public async Task WhenListingFails_NoOps_AndDoesNotThrow()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        using var provider = BuildProvider(new ThrowingOllamaModelService(), mapStore);

        await Backfill(provider);

        AssertEx.Empty(mapStore.Mappings);
    }

    [Test]
    public async Task WhenNoModelsInstalled_NoOps()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        using var provider = BuildProvider(new FakeOllamaModelService([]), mapStore);

        await Backfill(provider);

        AssertEx.Empty(mapStore.Mappings);
    }

    private static Task Backfill(ServiceProvider provider)
    {
        return OllamaProviderMapBackfillService.BackfillAsync(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
    }

    private static ServiceProvider BuildProvider(IOllamaModelService ollamaModelService, InMemoryCoordinatedModelProviderMapStore mapStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ollamaModelService);
        services.AddSingleton<IModelProviderMapLeaseCoordinator>(new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain()));
        services.AddScoped<ICoordinatedModelProviderMapStore>(_ => mapStore);
        services.AddSingleton(Substitute.For<ILocalModelProviderResolver>());
        services.AddSingleton<ILogger<OllamaProviderMapBackfillCoordinator>>(NullLogger<OllamaProviderMapBackfillCoordinator>.Instance);
        services.AddScoped<IOllamaProviderMapBackfillCoordinator, OllamaProviderMapBackfillCoordinator>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeOllamaModelService(IReadOnlyList<string> installedNames) : StubOllamaModelService
    {
        public override Task<IEnumerable<Model>> ListLocalModelsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IEnumerable<Model>>(installedNames.Select(name => new Model
            {
                Name = name
            }).ToArray());
        }
    }

    private sealed class ThrowingOllamaModelService : StubOllamaModelService
    {
        public override Task<IEnumerable<Model>> ListLocalModelsAsync(CancellationToken ct = default)
        {
            throw new HttpRequestException("Ollama is not running.");
        }
    }

    // Base stub: only ListLocalModelsAsync is exercised by the backfill; the rest throw so an accidental call is loud.
    private abstract class StubOllamaModelService : IOllamaModelService
    {
        public abstract Task<IEnumerable<Model>> ListLocalModelsAsync(CancellationToken ct = default);

        public Task<ShowModelResponse> ShowModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<PullModelResponse> PullModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task UnloadModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }

}
