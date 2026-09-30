namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="KnowledgeCompanionReserve" /> tests: companions are summed at the gate's footprint, resident ones skipped only
///     against measured free VRAM, unknown ones count zero, and <see cref="KnowledgeCompanionReserve.ApplyTo" /> keeps GPU mode.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class KnowledgeCompanionReserveTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;
    private const string Reranker = "gpustack/bge-reranker-v2-m3-GGUF:Q4_K_M";
    private const string Embedder = "nomic-ai/nomic-embed-text-v1.5-GGUF:F16";

    [Test]
    public async Task NonResidentReranker_AndEmbedder_AreBothReserved()
    {
        var harness = new Harness();

        var reserve = await harness.ResolveAsync();

        AssertEx.Equal((1300 * Mib) + (1100 * Mib), reserve);
    }

    [Test]
    public async Task ResidentReranker_IsNotReservedTwice_AgainstMeasuredFreeVram()
    {
        var harness = new Harness
        {
            Resident = [ResidentReranker]
        };

        var reserve = await harness.ResolveAsync();

        AssertEx.Equal(1100 * Mib, reserve);
        await harness.Footprints.DidNotReceive()
                     .ResolveFootprintAsync(Reranker, ModelRole.Reranker, Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResidentReranker_IsStillReserved_WhenOnlyTotalVramIsKnown()
    {
        // Total VRAM does not net out a resident process, so skipping it there would count the companion nowhere.
        var harness = new Harness
        {
            Resident = [ResidentReranker],
            Profile = MeasuredProfile with
            {
                AvailableVramBytes = null
            }
        };

        var reserve = await harness.ResolveAsync();

        AssertEx.Equal((1300 * Mib) + (1100 * Mib), reserve);
    }

    [Test]
    public async Task NoRerankerConfigured_AndAnOllamaEmbedder_ReserveNothing()
    {
        var harness = new Harness
        {
            RerankerModelName = string.Empty,
            EmbeddingProviderName = "ollama"
        };

        var reserve = await harness.ResolveAsync();

        AssertEx.Equal(0L, reserve);
        await harness.Footprints.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownFootprint_AndAnUnconfidentEmbedder_CountAsZero()
    {
        var harness = new Harness
        {
            RerankerFootprint = ModelFootprint.Unknown,
            EmbedderConfident = false
        };

        var reserve = await harness.ResolveAsync();

        AssertEx.Equal(0L, reserve);
    }

    [Test]
    public void ApplyTo_ReducesBothVramFigures_AndClampsToOneByte()
    {
        var profile = new HardwareProfile
        {
            TotalRamBytes = 64 * Gib,
            AvailableRamBytes = 48 * Gib,
            VramBytes = 16 * Gib,
            AvailableVramBytes = 8 * Gib,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gib
        };

        var reduced = KnowledgeCompanionReserve.ApplyTo(profile, 2 * Gib);
        var clamped = KnowledgeCompanionReserve.ApplyTo(profile, 100 * Gib);

        AssertEx.Equal(14 * Gib, reduced.VramBytes);
        AssertEx.Equal(6 * Gib, reduced.AvailableVramBytes);
        AssertEx.Equal(1L, clamped.VramBytes);
        AssertEx.Equal(1L, clamped.AvailableVramBytes);
        AssertEx.True(clamped is { GpuAccelAvailable: true, VramKnown: true }, "The reserve must never flip GPU mode to CPU mode.");
        AssertEx.True(ReferenceEquals(profile, KnowledgeCompanionReserve.ApplyTo(profile, 0)));
    }

    [Test]
    public void ApplyTo_LeavesACpuProfileWithoutVramAlone()
    {
        var profile = new HardwareProfile
        {
            TotalRamBytes = 32 * Gib,
            AvailableRamBytes = 16 * Gib,
            VramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Unknown,
            GpuAccelAvailable = false,
            CpuCores = 8,
            FreeDiskBytes = 100 * Gib
        };

        var result = KnowledgeCompanionReserve.ApplyTo(profile, 2 * Gib);

        AssertEx.Null(result.VramBytes);
        AssertEx.Null(result.AvailableVramBytes);
        AssertEx.Equal(16 * Gib, result.AvailableRamBytes);
    }

    private static readonly LlamaServerRunningProcess ResidentReranker = new()
    {
        ModelName = Reranker.ToUpperInvariant(),
        Role = ModelRole.Reranker,
        LastUsedUtc = DateTimeOffset.UnixEpoch
    };

    private static readonly HardwareProfile MeasuredProfile = new()
    {
        TotalRamBytes = 64 * Gib,
        AvailableRamBytes = 48 * Gib,
        VramBytes = 32 * Gib,
        AvailableVramBytes = 30 * Gib,
        VramKnown = true,
        GpuVendor = GpuVendor.Nvidia,
        GpuAccelAvailable = true,
        CpuCores = 16,
        FreeDiskBytes = 500 * Gib
    };

    private sealed class Harness
    {
        public HardwareProfile Profile { get; init; } = MeasuredProfile;
        public string RerankerModelName { get; init; } = Reranker;
        public string EmbeddingProviderName { get; init; } = LlamaServerProviderConstants.ProviderName;
        public bool EmbedderConfident { get; init; } = true;
        public ModelFootprint RerankerFootprint { get; init; } = ModelFootprint.Known(new ResourceFootprint(1300 * Mib, RamBytes: 0));
        public IReadOnlyList<LlamaServerRunningProcess> Resident { get; init; } = [];
        public IModelFootprintProvider Footprints { get; } = Substitute.For<IModelFootprintProvider>();

        public KnowledgeCompanionReserve Build()
        {
            Footprints.ResolveFootprintAsync(Reranker, ModelRole.Reranker, Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(RerankerFootprint));
            Footprints.ResolveFootprintAsync(Embedder, ModelRole.Embedding, Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(ModelFootprint.Known(new ResourceFootprint(1100 * Mib, RamBytes: 0))));

            var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
            supervisor.ListRunningProcesses().Returns(Resident);

            var provider = Substitute.For<ILocalModelProvider>();
            var providerResolver = Substitute.For<ILocalModelProviderResolver>();
            providerResolver.ResolveProvider(Arg.Any<string>()).Returns(provider);
            var embeddingResolver = Substitute.For<IEmbeddingModelResolver>();
            embeddingResolver.ResolveAsync(provider, Arg.Any<CancellationToken>())
                             .Returns(Task.FromResult(new EmbeddingModelResolution
                             {
                                 Name = Embedder,
                                 IsConfident = EmbedderConfident
                             }));

            return new KnowledgeCompanionReserve(Footprints,
                supervisor,
                providerResolver,
                embeddingResolver,
                Options.Create(new KnowledgeBaseOptions
                {
                    RerankerModelName = RerankerModelName,
                    EmbeddingProviderName = EmbeddingProviderName
                }),
                NullLogger<KnowledgeCompanionReserve>.Instance);
        }

        public Task<long> ResolveAsync() =>
            Build().ResolveGpuBytesAsync(Profile, CancellationToken.None);
    }
}
