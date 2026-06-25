namespace XE_Local_AI_Engine.Tests.Capacity;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFootprintProvider" /> tests: the provider sources the quant label + on-disk size from the GGUF
///     registry seam and the weight/KV inputs from the header read (both via <see cref="IGgufModelStore" />), then scores
///     them with the pure <see cref="MemoryFitEstimator" />. Asserts the registry quant drives the density (HIGH-3, never
///     the header file_type), the header-facts weights+KV path, the file-size fallback, and the Unknown→not-installed
///     branch. No I/O — the store seam is mocked.
/// </summary>
public sealed class ModelFootprintProviderTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Model = "bartowski/Some-Model-GGUF:Q6_K";

    [Test]
    public async Task Footprint_UsesRegistryQuantLabel_NotHeaderFileType()
    {
        // The store returns a Q6_K registry quant. The footprint must price at the Q6_K density — strictly higher than
        // the Q4_K_M default an unknown/file_type-derived quant would fall back to.
        var profile = GpuProfile(64 * Gb);
        const long paramCount = 1_000_000_000L;

        var providerQ6 = BuildProvider(FootprintFacts("Q6_K", paramCount: paramCount));
        var providerDefault = BuildProvider(FootprintFacts("Q4_K_M", paramCount: paramCount));

        var q6 = await providerQ6.ResolveFootprintAsync(Model, profile, CancellationToken.None);
        var q4 = await providerDefault.ResolveFootprintAsync(Model, profile, CancellationToken.None);

        AssertEx.True(q6.IsKnown);
        AssertEx.True(q4.IsKnown);
        // Q6_K (~6.5625 bits/weight) is denser than Q4_K_M (~4.5), so the same param count weighs more.
        AssertEx.True(q6.EstimatedBytes > q4.EstimatedBytes,
            "a Q6_K model must price above the Q4_K_M default — the registry quant, not the header file_type, drives density.");

        var expectedWeights = (long)(paramCount * MemoryFitEstimator.BytesPerWeight("Q6_K"));
        AssertEx.True(q6.EstimatedBytes > expectedWeights, "the estimate must include weights + KV + overhead.");
    }

    [Test]
    public async Task Footprint_FromHeaderFacts_ComputesWeightsPlusKv()
    {
        var profile = GpuProfile(64 * Gb);
        var facts = new GgufModelFootprintFacts("Q4_K_M",
            FileSizeBytes: 2 * Gb,
            ParamCount: 1_000_000_000L,
            BlockCount: 4,
            AttentionHeadCount: 4,
            AttentionHeadCountKV: 2,
            EmbeddingLength: 16,
            ContextLength: 2048);
        var provider = BuildProvider(facts);

        var footprint = await provider.ResolveFootprintAsync(Model, profile, CancellationToken.None);

        // Re-derive the estimator output for the same inputs (ctxTarget = min(2048, 8192) = 2048).
        var expected = new MemoryFitEstimator().Estimate("Q4_K_M",
            paramCount: 1_000_000_000L,
            fileSizeBytes: 2 * Gb,
            blockCount: 4,
            attentionHeadCountKV: 2,
            embeddingLength: 16,
            attentionHeadCount: 4,
            ctxTarget: 2048,
            profile,
            kvCacheQuantized: false);

        AssertEx.True(footprint.IsKnown);
        AssertEx.Equal(expected.EstimatedBytes, footprint.EstimatedBytes);
    }

    [Test]
    public async Task Footprint_FromFileSize_WhenNoHeaderFacts()
    {
        // No param count → the weights term falls back to the on-disk file size.
        var profile = GpuProfile(64 * Gb);
        var facts = FootprintFacts("Q4_K_M", paramCount: null, fileSizeBytes: 3 * Gb);
        var provider = BuildProvider(facts);

        var footprint = await provider.ResolveFootprintAsync(Model, profile, CancellationToken.None);

        AssertEx.True(footprint.IsKnown);
        AssertEx.True(footprint.EstimatedBytes >= 3 * Gb, "the file-size fallback must drive the weights term.");
    }

    [Test]
    public async Task Footprint_WhenNotInstalled_ReturnsUnknown()
    {
        var profile = GpuProfile(64 * Gb);
        var store = Substitute.For<IGgufModelStore>();
        store.ResolveModelFootprintFactsAsync(Model, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<GgufModelFootprintFacts?>(null));
        var provider = new ModelFootprintProvider(store, new MemoryFitEstimator());

        var footprint = await provider.ResolveFootprintAsync(Model, profile, CancellationToken.None);

        AssertEx.False(footprint.IsKnown, "a model with no registry entry must be Unknown so the gate rejects.");
    }

    [Test]
    public async Task Footprint_WhenNoHeaderAndNoFileSize_ReturnsUnknown()
    {
        var profile = GpuProfile(64 * Gb);
        var facts = FootprintFacts("Q4_K_M", paramCount: null, fileSizeBytes: 0);
        var provider = BuildProvider(facts);

        var footprint = await provider.ResolveFootprintAsync(Model, profile, CancellationToken.None);

        AssertEx.False(footprint.IsKnown, "no param count and no file size leaves nothing to estimate weights from.");
    }

    [Test]
    public async Task Footprint_StripsDynamicQuantPrefix_BeforeDensityMapping()
    {
        // UD-Q6_K must price off its base Q6_K density, not collapse to the Q4_K_M default.
        var profile = GpuProfile(64 * Gb);
        const long paramCount = 1_000_000_000L;
        var dynamic = BuildProvider(FootprintFacts("UD-Q6_K", paramCount: paramCount));
        var baseQ6 = BuildProvider(FootprintFacts("Q6_K", paramCount: paramCount));

        var dyn = await dynamic.ResolveFootprintAsync(Model, profile, CancellationToken.None);
        var plain = await baseQ6.ResolveFootprintAsync(Model, profile, CancellationToken.None);

        AssertEx.Equal(plain.EstimatedBytes, dyn.EstimatedBytes);
    }

    private static ModelFootprintProvider BuildProvider(GgufModelFootprintFacts facts)
    {
        var store = Substitute.For<IGgufModelStore>();
        store.ResolveModelFootprintFactsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<GgufModelFootprintFacts?>(facts));
        return new ModelFootprintProvider(store, new MemoryFitEstimator());
    }

    private static GgufModelFootprintFacts FootprintFacts(string quant, long? paramCount, long fileSizeBytes = 2L * 1024 * 1024 * 1024)
    {
        return new GgufModelFootprintFacts(quant,
            fileSizeBytes,
            paramCount,
            BlockCount: 4,
            AttentionHeadCount: 4,
            AttentionHeadCountKV: 2,
            EmbeddingLength: 16,
            ContextLength: 2048);
    }

    private static HardwareProfile GpuProfile(long vramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }
}
