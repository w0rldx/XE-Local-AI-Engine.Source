namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="MemoryFitEstimator" /> tests: the pure estimator computes
///     <c>weights + 2·layers·kv_heads·head_dim·ctx·bytes + margin + ~0.75GB overhead</c>, rejects a model that exceeds
///     the budget, lowers the KV term under KV-cache quant, and degrades to a RAM/CPU budget when VRAM is unknown. No
///     I/O — every input is supplied directly.
/// </summary>
public sealed class MemoryFitEstimatorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    // A small, exactly-computable model: 1B params, 4 layers, 2 kv-heads, embedding 16 over 4 heads (head_dim = 4).
    private const long ParamCount = 1_000_000_000L;
    private const long BlockCount = 4L;
    private const long KvHeads = 2L;
    private const long EmbeddingLength = 16L;
    private const long HeadCount = 4L;
    private const long CtxTarget = 2048L;

    [Test]
    public void MemoryFit_Estimates_WeightsPlusKvPlusOverhead()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("Q4_K_M",
            ParamCount,
            fileSizeBytes: 0,
            BlockCount,
            KvHeads,
            EmbeddingLength,
            HeadCount,
            CtxTarget,
            profile,
            kvCacheQuantized: false);

        // weights = params · 4.5/8 bytes-per-weight.
        var weights = (long)(ParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        // head_dim = 16/4 = 4; kv = 2 · 4 · 2 · 4 · 2048 · 2 bytes (fp16).
        var kv = (long)(2d * BlockCount * KvHeads * (EmbeddingLength / (double)HeadCount) * CtxTarget * 2d);
        var margin = (long)((weights + kv) * 0.12d);
        var expected = weights + kv + margin + MemoryFitEstimator.RuntimeOverheadBytes;

        AssertEx.Equal(expected, estimate.EstimatedBytes);
        AssertEx.True(estimate.Fits, "the small model must fit a 64 GB budget.");
        AssertEx.Equal(FitMode.Gpu, estimate.Mode);
        AssertEx.Equal(profile.VramBytes!.Value - expected, estimate.HeadroomBytes);
    }

    [Test]
    public void MemoryFit_RejectsModel_WhenExceedsBudget()
    {
        // A 70B model cannot fit a 4 GB VRAM budget at Q4_K_M (~40 GB weights alone).
        var tightProfile = GpuProfile(4 * Gb);
        var roomyProfile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var tooBig = estimator.Estimate("Q4_K_M", paramCount: 70_000_000_000L, fileSizeBytes: 0, blockCount: 80, attentionHeadCountKV: 8, embeddingLength: 8192, attentionHeadCount: 64, CtxTarget,
            tightProfile, kvCacheQuantized: false);
        var fits = estimator.Estimate("Q4_K_M", paramCount: 70_000_000_000L, fileSizeBytes: 0, blockCount: 80, attentionHeadCountKV: 8, embeddingLength: 8192, attentionHeadCount: 64, CtxTarget,
            roomyProfile, kvCacheQuantized: false);

        AssertEx.False(tooBig.Fits, "a 70B model must not fit a 4 GB VRAM budget.");
        AssertEx.True(tooBig.HeadroomBytes < 0, "headroom must be negative when the model exceeds the budget.");
        AssertEx.True(fits.Fits, "the same 70B model must fit a 64 GB budget.");
    }

    [Test]
    public void MemoryFit_KvCacheQuant_LowersKvTerm()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var fp16 = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false);
        var quantized = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: true);

        AssertEx.True(quantized.EstimatedBytes < fp16.EstimatedBytes,
            "an 8-bit KV cache must lower the total below the fp16 estimate.");
    }

    [Test]
    public void MemoryFit_VramUnknown_UsesRamBudget_CpuMode()
    {
        // VRAM unknown ⇒ budget = available RAM, mode = Cpu (the degrade rule when no GPU memory is known).
        var cpuProfile = new HardwareProfile
        {
            TotalRamBytes = 32 * Gb,
            AvailableRamBytes = 24 * Gb,
            VramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Unknown,
            GpuAccelAvailable = false,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, cpuProfile, kvCacheQuantized: false);

        AssertEx.Equal(FitMode.Cpu, estimate.Mode);
        AssertEx.True(estimate.Fits, "the small model fits the 24 GB available-RAM budget.");
        AssertEx.Equal(cpuProfile.AvailableRamBytes - estimate.EstimatedBytes, estimate.HeadroomBytes);
    }

    [Test]
    public void MemoryFit_NoParamCount_FallsBackToFileSize()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("Q4_K_M",
            paramCount: null,
            2 * Gb,
            BlockCount,
            KvHeads,
            EmbeddingLength,
            HeadCount,
            CtxTarget,
            profile,
            kvCacheQuantized: false);

        AssertEx.True(estimate.EstimatedBytes > 2 * Gb, "the weights fallback must use the on-disk file size.");
        AssertEx.True(estimate.Fits);
    }

    [Test]
    public void MemoryFit_GqaHeadMath_HeadDimUsesHeadCount_NotKvHeadCount()
    {
        // GQA: 4x more query heads than kv heads. head_dim = embedding_length / attention_head_count (16),
        // NOT / attention_head_count_kv — isolate the KV term by leaving weights at 0 (no paramCount, no file size).
        const long blockCount = 8L;
        const long kvHeads = 4L;
        const long headCount = 16L; // 4:1 GQA ratio.
        const long embeddingLength = 64L; // head_dim = 64 / 16 = 4.
        const long ctx = 4096L;

        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("Q4_K_M", paramCount: null, fileSizeBytes: 0, blockCount, kvHeads, embeddingLength, headCount, ctx, profile, kvCacheQuantized: false);

        var headDim = embeddingLength / (double)headCount;
        var kv = (long)(2d * blockCount * kvHeads * headDim * ctx * 2d);
        var margin = (long)(kv * 0.12d);
        var expected = kv + margin + MemoryFitEstimator.RuntimeOverheadBytes;

        AssertEx.Equal(expected, estimate.EstimatedBytes);
    }

    [Test]
    public void MemoryFit_KvCacheQuant_Q8_UsesOneBytePerElement()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false,
            kvCacheQuant: KvCacheQuant.Q8_0);

        var weights = (long)(ParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var kv = (long)(2d * BlockCount * KvHeads * (EmbeddingLength / (double)HeadCount) * CtxTarget * 1d);
        var margin = (long)((weights + kv) * 0.12d);
        var expected = weights + kv + margin + MemoryFitEstimator.RuntimeOverheadBytes;

        AssertEx.Equal(expected, estimate.EstimatedBytes);
    }

    [Test]
    public void MemoryFit_KvCacheQuant_Q4_UsesHalfBytePerElement()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false,
            kvCacheQuant: KvCacheQuant.Q4_0);

        var weights = (long)(ParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var kv = (long)(2d * BlockCount * KvHeads * (EmbeddingLength / (double)HeadCount) * CtxTarget * 0.5d);
        var margin = (long)((weights + kv) * 0.12d);
        var expected = weights + kv + margin + MemoryFitEstimator.RuntimeOverheadBytes;

        AssertEx.Equal(expected, estimate.EstimatedBytes);
    }

    [Test]
    public void MemoryFit_KvCacheQuantOverride_TakesPrecedenceOverLegacyBoolFlag()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        // Legacy bool asks for the quantized (1 byte/elem) path, but the explicit override says F16 — override wins.
        var overridden = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: true,
            kvCacheQuant: KvCacheQuant.F16);
        var fp16Baseline = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false);

        AssertEx.Equal(fp16Baseline.EstimatedBytes, overridden.EstimatedBytes);
    }

    [Test]
    public void MemoryFit_MoeFactsNull_BehavesIdenticallyToOmittingTheParameter()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var withExplicitNull = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false, moeFacts: null);
        var withOmittedParam = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false);

        AssertEx.Equal(withOmittedParam, withExplicitNull);
        AssertEx.Equal(MoeFitVerdict.FitsResident, withExplicitNull.MoeVerdict);
        AssertEx.Null(withExplicitNull.GpuBytes);
        AssertEx.Null(withExplicitNull.CpuBytes);
        AssertEx.False(withExplicitNull.ExpertsOffloaded);
    }

    [Test]
    public void MemoryFit_Moe_FitsResident_WhenSmallEnoughWithoutOffload()
    {
        // A small MoE model that already fits the resident budget must not engage the offload path at all.
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();
        var moeFacts = new MoeFacts(ActiveParamCount: 200_000_000L, ExpertCount: 8, ExpertUsedCount: 2);

        var estimate = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false, moeFacts);

        AssertEx.True(estimate.Fits);
        AssertEx.Equal(MoeFitVerdict.FitsResident, estimate.MoeVerdict);
        AssertEx.Null(estimate.GpuBytes);
        AssertEx.Null(estimate.CpuBytes);
        AssertEx.False(estimate.ExpertsOffloaded);
    }

    [Test]
    public void MemoryFit_Moe_FitsWithExpertOffload_WhenResidentExceedsButSplitFitsGpuAndRam()
    {
        // 30B total / 3B active (a "35B-A3B"-style MoE), Q4_K_M: weights alone (~15.7 GB) exceed an 8 GB VRAM budget,
        // but the non-expert (~10%) + KV slice fits VRAM while the expert slice (~90%) fits the 48 GB RAM budget.
        const long totalParamCount = 30_000_000_000L;
        const long activeParamCount = 3_000_000_000L;

        var profile = GpuProfile(8 * Gb);
        var estimator = new MemoryFitEstimator();
        var moeFacts = new MoeFacts(activeParamCount, ExpertCount: 8, ExpertUsedCount: 2);

        var estimate = estimator.Estimate("Q4_K_M", totalParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false, moeFacts);

        var weights = (long)(totalParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var kv = (long)(2d * BlockCount * KvHeads * (EmbeddingLength / (double)HeadCount) * CtxTarget * 2d);
        var residentMargin = (long)((weights + kv) * 0.12d);
        var residentEstimated = weights + kv + residentMargin + MemoryFitEstimator.RuntimeOverheadBytes;
        AssertEx.True(residentEstimated > profile.VramBytes!.Value, "the resident estimate must exceed the 8 GB VRAM budget for this test to exercise the offload path.");

        var expertParamFraction = (totalParamCount - activeParamCount) / (double)totalParamCount;
        var expertWeightsBytes = (long)(weights * expertParamFraction);
        var nonExpertWeightsBytes = weights - expertWeightsBytes;
        var gpuMargin = (long)((nonExpertWeightsBytes + kv) * 0.12d);
        var expectedGpuBytes = nonExpertWeightsBytes + kv + gpuMargin + MemoryFitEstimator.RuntimeOverheadBytes;
        var expectedCpuBytes = expertWeightsBytes;

        AssertEx.True(estimate.Fits, "the offload split must fit even though the resident estimate did not.");
        AssertEx.Equal(MoeFitVerdict.FitsWithExpertOffload, estimate.MoeVerdict);
        AssertEx.True(estimate.ExpertsOffloaded);
        AssertEx.Equal(expectedGpuBytes, estimate.GpuBytes);
        AssertEx.Equal(expectedCpuBytes, estimate.CpuBytes);
        AssertEx.Equal(expectedGpuBytes + expectedCpuBytes, estimate.EstimatedBytes);
        AssertEx.Equal(profile.VramBytes!.Value - expectedGpuBytes, estimate.HeadroomBytes);
        AssertEx.Equal(FitMode.Gpu, estimate.Mode);
    }

    [Test]
    public void MemoryFit_Moe_UsesDefaultExpertShare_WhenActiveParamCountUnknown()
    {
        // Only ExpertCount/ExpertUsedCount known (no published active-param spec) → the conservative documented default
        // expert-weight-share fraction drives the split instead of the total−active approximation.
        const long totalParamCount = 30_000_000_000L;

        var profile = GpuProfile(8 * Gb);
        var estimator = new MemoryFitEstimator();
        var moeFacts = new MoeFacts(ActiveParamCount: null, ExpertCount: 8, ExpertUsedCount: 2);

        var estimate = estimator.Estimate("Q4_K_M", totalParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false, moeFacts);

        var weights = (long)(totalParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var kv = (long)(2d * BlockCount * KvHeads * (EmbeddingLength / (double)HeadCount) * CtxTarget * 2d);
        var expertWeightsBytes = (long)(weights * MemoryFitEstimator.DefaultExpertWeightShareFraction);
        var nonExpertWeightsBytes = weights - expertWeightsBytes;
        var gpuMargin = (long)((nonExpertWeightsBytes + kv) * 0.12d);
        var expectedGpuBytes = nonExpertWeightsBytes + kv + gpuMargin + MemoryFitEstimator.RuntimeOverheadBytes;

        AssertEx.True(estimate.Fits);
        AssertEx.Equal(MoeFitVerdict.FitsWithExpertOffload, estimate.MoeVerdict);
        AssertEx.Equal(expectedGpuBytes, estimate.GpuBytes);
        AssertEx.Equal(expertWeightsBytes, estimate.CpuBytes);
    }

    [Test]
    public void MemoryFit_Moe_DoesNotFit_WhenNeitherResidentNorOffloadBudgetFits()
    {
        // Same 30B/3B-active MoE model, but both VRAM and available RAM are too small for even the expert-offload split.
        const long totalParamCount = 30_000_000_000L;
        const long activeParamCount = 3_000_000_000L;

        var tinyProfile = new HardwareProfile
        {
            TotalRamBytes = 4 * Gb,
            AvailableRamBytes = 2 * Gb,
            VramBytes = 4 * Gb,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 8,
            FreeDiskBytes = 500 * Gb
        };
        var estimator = new MemoryFitEstimator();
        var moeFacts = new MoeFacts(activeParamCount, ExpertCount: 8, ExpertUsedCount: 2);

        var estimate = estimator.Estimate("Q4_K_M", totalParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, tinyProfile, kvCacheQuantized: false, moeFacts);

        AssertEx.False(estimate.Fits, "neither the resident nor the expert-offload budget can fit this model.");
        AssertEx.Equal(MoeFitVerdict.DoesNotFit, estimate.MoeVerdict);
        AssertEx.Null(estimate.GpuBytes);
        AssertEx.Null(estimate.CpuBytes);
        AssertEx.False(estimate.ExpertsOffloaded);
    }

    [Test]
    public void MemoryFit_Moe_OffloadNotAttempted_WhenNoGpuAcceleration()
    {
        // MoE facts present, but the node has no GPU acceleration — the offload path (GPU-resident non-expert + KV) is
        // only meaningful with a VRAM budget, so a resident-exceeds-budget model must reject rather than "offload".
        const long totalParamCount = 30_000_000_000L;
        var cpuProfile = new HardwareProfile
        {
            TotalRamBytes = 8 * Gb,
            AvailableRamBytes = 4 * Gb,
            VramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Unknown,
            GpuAccelAvailable = false,
            CpuCores = 8,
            FreeDiskBytes = 500 * Gb
        };
        var estimator = new MemoryFitEstimator();
        var moeFacts = new MoeFacts(ActiveParamCount: 3_000_000_000L, ExpertCount: 8, ExpertUsedCount: 2);

        var estimate = estimator.Estimate("Q4_K_M", totalParamCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, cpuProfile, kvCacheQuantized: false, moeFacts);

        AssertEx.False(estimate.Fits);
        AssertEx.Equal(MoeFitVerdict.DoesNotFit, estimate.MoeVerdict);
        AssertEx.Equal(FitMode.Cpu, estimate.Mode);
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
