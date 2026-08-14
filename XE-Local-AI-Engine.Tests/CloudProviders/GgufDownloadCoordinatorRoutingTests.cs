namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the GGUF download path writes the <c>model_provider_map</c> "llamacpp" row for the canonical model name on
///     a successful download — the single production writer that makes a downloaded GGUF reach the llama.cpp runtime
///     regardless of the unmapped-routing default. A failed download writes NO mapping row.
/// </summary>
public sealed class GgufDownloadCoordinatorRoutingTests
{
    private const string Repo = "bartowski/Qwen2.5-0.5B-Instruct-GGUF";
    private const string Quant = "Q4_K_M";

    [Test]
    public async Task SuccessfulDownload_WritesLlamaCppMapRow_ForCanonicalName()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        var store = new ProvisioningModelStore();
        var coordinator = BuildCoordinator(store, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);

        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Completed);

        var canonical = GgufModelName.Format(Repo, Quant);
        AssertEx.Equal(canonical, ticket.ModelName);
        AssertEx.NotEqual(Guid.Empty, ticket.OperationId);
        AssertEx.Equal("Download", ticket.OperationKind);
        AssertEx.True(mapStore.Mappings.TryGetValue(canonical, out var mapping), "a map row must be written for the canonical name");
        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, mapping!.ProviderName);
    }

    [Test]
    public async Task FailedDownload_WritesNoMapRow()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        var store = new ProvisioningModelStore
        {
            FailDownload = true
        };
        var coordinator = BuildCoordinator(store, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);

        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Failed);

        AssertEx.Equal(expected: 0, mapStore.Mappings.Count);
    }

    private static GgufDownloadCoordinator BuildCoordinator(IGgufModelStore store, ICoordinatedModelProviderMapStore mapStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModelProviderMapLeaseCoordinator>(new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain()));
        services.AddScoped(_ => mapStore);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new GgufDownloadCoordinator(store,
            scopeFactory,
            new GgufAcquisitionOperationRegistry(TimeProvider.System),
            new NullGgufDownloadEventPublisher(),
            NullLogger<GgufDownloadCoordinator>.Instance);
    }

    private static async Task WaitForPhaseAsync(IGgufDownloadCoordinator coordinator, string modelName, GgufDownloadPhase phase)
    {
        // The download runs detached; poll its status until it reaches the terminal phase (bounded so a hung test fails
        // fast rather than hanging the suite).
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (coordinator.GetStatus(modelName)?.Phase == phase)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Download for '{modelName}' did not reach phase {phase}.");
    }

    /// <summary>A GGUF store that resolves the canonical name and "downloads" instantly (or fails on request).</summary>
    private sealed class ProvisioningModelStore : IGgufModelStore
    {
        public bool FailDownload { get; init; }

        public Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<string?>("/fake/m.gguf");
        }

        public Task<string?> ResolveProjectorFilePathAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([]);
        }

        public Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct)
        {
            return Task.FromResult(GgufModelName.Format(request.RepoId, request.Quant ?? Quant));
        }

        public Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            if (FailDownload)
            {
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network, "Download failed.");
            }

            var quant = request.Quant ?? Quant;
            var name = GgufModelName.Format(request.RepoId, quant);
            return Task.FromResult(new GgufModelHandle(name, "/fake/m.gguf", quant, SizeBytes: 1, Sha256: null, "rev", GgufRole.Chat));
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        public Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<GgufModelFootprintFacts?>(null);
        }
    }

}
