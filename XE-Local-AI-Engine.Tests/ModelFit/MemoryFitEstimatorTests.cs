namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
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
        AssertEx.Equal(profile.VramBytes.Value - expectedGpuBytes, estimate.HeadroomBytes);
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

    [Test]
    public void MemoryFit_ExplicitKeyValueLength_OverridesDerivedHeadDim_Qwen3()
    {
        // Qwen3-family pins head_dim independently of the embedding width, so the derived
        // head_dim = embedding_length / n_heads UNDER-estimates the KV cache. Here embedding 1024 / 32 heads = derived 32,
        // while the explicit key/value length is 128 (4× the derived per-head dimension).
        const long paramCount = 600_000_000L;
        const long blockCount = 28L;
        const long kvHeads = 8L;
        const long embedding = 1024L;
        const long headCount = 32L; // derived head_dim = 1024 / 32 = 32
        const long ctx = 4096L;

        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var derived = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, blockCount, kvHeads, embedding, headCount, ctx, profile, kvCacheQuantized: false);
        var explicitShape = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, blockCount, kvHeads, embedding, headCount, ctx, profile, kvCacheQuantized: false,
            attention: new GgufAttentionShape(KeyLength: 128, ValueLength: 128));

        // Weights are identical (same param count) → the whole delta is the KV term.
        var weights = (long)(paramCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M")); // 600e6 · 0.5625 = 337_500_000
        // Explicit KV: n_kv_heads · (128+128) · 2 bytes(fp16) · layers · ctx = 8·256·2 · 28·4096 = 4096 · 114688 = 469_762_048.
        var perLayerExplicit = kvHeads * (128d + 128d) * 2d;
        var kvExplicit = (long)(perLayerExplicit * (blockCount * (double)ctx));
        var marginExplicit = (long)((weights + kvExplicit) * 0.12d);
        var expectedExplicit = weights + kvExplicit + marginExplicit + MemoryFitEstimator.RuntimeOverheadBytes;

        // Derived KV: head_dim 32 → 8·64·2 · 28·4096 = 1024 · 114688 = 117_440_512 (exactly a quarter of the explicit KV).
        var perLayerDerived = kvHeads * ((embedding / (double)headCount) + (embedding / (double)headCount)) * 2d;
        var kvDerived = (long)(perLayerDerived * (blockCount * (double)ctx));

        AssertEx.Equal(expectedExplicit, explicitShape.EstimatedBytes);
        AssertEx.True(explicitShape.EstimatedBytes > derived.EstimatedBytes, "explicit key/value length must raise the KV estimate above the derived head_dim.");
        AssertEx.Equal(kvExplicit, 4L * kvDerived); // 128/32 = 4× per-head correction, weights unchanged.
        AssertEx.Equal(FitConfidence.Exact, explicitShape.Confidence); // explicit key/value + param count ⇒ Exact
        AssertEx.Equal(FitConfidence.Approximate, derived.Confidence); // derived head_dim ⇒ Approximate
    }

    [Test]
    public void MemoryFit_SlidingWindowAttention_CapsWindowLimitedLayers_Gemma3()
    {
        // Gemma3-12B-like — 48 layers, key/value length 256, sliding_window 1024, pattern 6 (5:1 local:global).
        // At ctx 8192 only the 8 global layers hold the full context; the other 40 window-limited layers hold ≤ 1024
        // positions, cutting the KV cache far below the naive "every layer × full ctx" figure.
        const long paramCount = 12_000_000_000L;
        const long blockCount = 48L;
        const long kvHeads = 8L;
        const long keyValueLen = 256L;
        const long ctx = 8192L;
        const long window = 1024L;
        const long pattern = 6L;

        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        // No window ⇒ every layer holds the full context (the naive figure the old estimator always used).
        var naive = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, blockCount, kvHeads, embeddingLength: 0, attentionHeadCount: 0, ctx, profile, kvCacheQuantized: false,
            attention: new GgufAttentionShape(KeyLength: keyValueLen, ValueLength: keyValueLen));
        var swa = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, blockCount, kvHeads, embeddingLength: 0, attentionHeadCount: 0, ctx, profile, kvCacheQuantized: false,
            attention: new GgufAttentionShape(KeyLength: keyValueLen, ValueLength: keyValueLen, SlidingWindow: window, SlidingWindowPattern: pattern));

        var weights = (long)(paramCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var perLayer = kvHeads * (keyValueLen + keyValueLen) * 2d; // 8 · 512 · 2 = 8192

        // Naive: 48 layers · 8192 ctx = 393216 token-layers → 8192 · 393216 = 3_221_225_472.
        var kvNaive = (long)(perLayer * (blockCount * (double)ctx));
        var expectedNaive = weights + kvNaive + (long)((weights + kvNaive) * 0.12d) + MemoryFitEstimator.RuntimeOverheadBytes;
        // SWA: ceil(48/6)=8 global · 8192 + 40 local · 1024 = 65536 + 40960 = 106496 token-layers → 8192 · 106496 = 872_415_232.
        var globalLayers = (blockCount + pattern - 1) / pattern;
        var swaTokens = (globalLayers * (double)ctx) + ((blockCount - globalLayers) * (double)window);
        var kvSwa = (long)(perLayer * swaTokens);
        var marginSwa = (long)((weights + kvSwa) * 0.12d);
        var expectedSwa = weights + kvSwa + marginSwa + MemoryFitEstimator.RuntimeOverheadBytes;

        AssertEx.Equal(expectedNaive, naive.EstimatedBytes);
        AssertEx.Equal(expectedSwa, swa.EstimatedBytes);
        AssertEx.True(swa.EstimatedBytes < naive.EstimatedBytes, "SWA must reduce the total estimate vs the naive full-context KV.");
        AssertEx.True(kvSwa < kvNaive, "SWA must cap the window-limited layers' KV cache.");
        AssertEx.True(kvNaive > kvSwa * 3, "the SWA KV must be well under a third of the naive KV at long context.");
        AssertEx.Equal(FitConfidence.Exact, swa.Confidence); // explicit key/value + param count ⇒ Exact
    }

    [Test]
    public void MemoryFit_DenseLlama_ExplicitKeyValueEqualsDerived_SameBytes_ButExactConfidence()
    {
        // A dense Llama-3-8B-like model whose derived head_dim (4096/32 = 128) already equals the explicit key/value
        // length: the byte estimate is identical either way — only the confidence differs (explicit ⇒ Exact).
        const long paramCount = 8_000_000_000L;
        const long blockCount = 32L;
        const long kvHeads = 8L;
        const long embedding = 4096L;
        const long headCount = 32L; // derived head_dim = 128
        const long ctx = 8192L;

        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var derived = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, blockCount, kvHeads, embedding, headCount, ctx, profile, kvCacheQuantized: false);
        var explicitShape = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, blockCount, kvHeads, embedding, headCount, ctx, profile, kvCacheQuantized: false,
            attention: new GgufAttentionShape(KeyLength: 128, ValueLength: 128));

        AssertEx.Equal(derived.EstimatedBytes, explicitShape.EstimatedBytes);
        AssertEx.Equal(FitConfidence.Approximate, derived.Confidence);
        AssertEx.Equal(FitConfidence.Exact, explicitShape.Confidence);
    }

    [Test]
    public void MemoryFit_MxFp4_SizedAtNativeDensity_AndFlaggedNative()
    {
        // gpt-oss ships native MXFP4 (~4.25 bits/weight). The estimate must price it at that density (not the 4.5bpw
        // unknown-quant default) and flag it native so the advisor never recommends a higher-quant requant of it.
        const long paramCount = 20_000_000_000L;
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("MXFP4", paramCount, fileSizeBytes: 0, blockCount: 24, attentionHeadCountKV: 8, embeddingLength: 2880, attentionHeadCount: 64,
            ctxTarget: 8192, profile, kvCacheQuantized: false, nativeQuantFormat: true);

        AssertEx.Equal(4.25d / 8d, MemoryFitEstimator.BytesPerWeight("MXFP4"));
        AssertEx.True(estimate.NativeQuantFormat, "an MXFP4 estimate must carry the native-format flag.");
        var weights = (long)(paramCount * (4.25d / 8d));
        AssertEx.True(estimate.EstimatedBytes > weights, "the estimate includes weights at MXFP4 density plus KV and overhead.");
    }

    [Test]
    public void FilterOutNativeFormatRequants_DropsHigherQuantRequants_KeepsNative()
    {
        // A gpt-oss repo shipping native MXFP4 plus bartowski Q8_0/Q4_K_M requants: both must be dropped so the advisor
        // recommends the native file, never a bloated lossy "upgrade". Q8_0 goes on quality rank; Q4_K_M goes on width —
        // it ranks BELOW native FP4 on the ladder yet is wider (4.5 vs 4.25 bits/weight), so re-encoding the native
        // weights into it costs space and quality at once.
        var candidates = new[]
        {
            ("MXFP4", QuantLadder.QualityRank("MXFP4")),
            ("Q8_0", QuantLadder.QualityRank("Q8_0")),
            ("Q4_K_M", QuantLadder.QualityRank("Q4_K_M"))
        };

        var kept = MemoryFitEstimator.FilterOutNativeFormatRequants(candidates, candidate => candidate.Item1, candidate => candidate.Item2);

        AssertEx.Equal(expected: 1, kept.Count);
        AssertEx.Equal("MXFP4", kept[0].Item1);
    }

    [Test]
    public void FilterOutNativeFormatRequants_KeepsGenuinelySmallerQuants_ForTightBoxes()
    {
        // The native cap is about pointless requants, not about hiding the repo from a small box: a quant that is
        // genuinely narrower than the native format is a real lower-quality option and must survive the guard.
        var candidates = new[]
        {
            ("NVFP4", QuantLadder.QualityRank("NVFP4")),
            ("Q6_K", QuantLadder.QualityRank("Q6_K")),
            ("Q3_K_M", QuantLadder.QualityRank("Q3_K_M")),
            ("Q2_K", QuantLadder.QualityRank("Q2_K"))
        };

        var kept = MemoryFitEstimator.FilterOutNativeFormatRequants(candidates, candidate => candidate.Item1, candidate => candidate.Item2)
                                     .Select(candidate => candidate.Item1)
                                     .ToList();

        AssertEx.Equal(expected: 3, kept.Count);
        AssertEx.Contains(kept, "NVFP4", message: "the native file itself is always kept.");
        AssertEx.Contains(kept, "Q3_K_M", message: "Q3_K_M is narrower than native FP4 — a real option, not a requant upgrade.");
        AssertEx.Contains(kept, "Q2_K", message: "Q2_K is narrower than native FP4 — a real option, not a requant upgrade.");
    }

    [Test]
    public void MemoryFit_GpuBudget_PrefersFreeVram_OverTotalVram()
    {
        // A 16 GiB card on a Windows desktop holds ~2.25 GiB for the compositor, browser and any warm sub-agent server
        // before the first model loads, leaving 13.75 GiB free. A dense 27B at Q3_K_M with an 8192-token window
        // estimates ~15.02 GiB: it clears TOTAL VRAM by ~0.98 GiB and misses FREE VRAM by ~1.27 GiB. Budgeting against
        // total scored it "recommended"; on WDDM the launch then demand-pages to host RAM with no OOM and no error, just
        // a several-fold slowdown that reads as an application fault.
        const long totalVram = 16 * Gb;
        const long freeVram = totalVram - (9 * Gb / 4); // 2.25 GiB already resident.
        const long paramCount = 27_000_000_000L;
        var attention = new GgufAttentionShape(KeyLength: 128, ValueLength: 128);
        var estimator = new MemoryFitEstimator();

        var againstTotal = estimator.Estimate("Q3_K_M", paramCount, fileSizeBytes: 0, blockCount: 62, attentionHeadCountKV: 8,
            embeddingLength: 5376, attentionHeadCount: 42, ctxTarget: 8192, GpuProfile(totalVram), kvCacheQuantized: false,
            attention: attention);
        var againstFree = estimator.Estimate("Q3_K_M", paramCount, fileSizeBytes: 0, blockCount: 62, attentionHeadCountKV: 8,
            embeddingLength: 5376, attentionHeadCount: 42, ctxTarget: 8192, GpuProfile(totalVram, freeVram), kvCacheQuantized: false,
            attention: attention);

        AssertEx.Equal(againstTotal.EstimatedBytes, againstFree.EstimatedBytes, "only the budget differs, not the footprint.");
        AssertEx.True(againstTotal.Fits, "the model does clear the card's TOTAL VRAM — the figure that produced the false recommendation.");
        AssertEx.False(againstFree.Fits, "it does not clear the VRAM actually free, so the advisor must not recommend it.");
        AssertEx.Equal(FitMode.Gpu, againstFree.Mode);
        AssertEx.Equal(freeVram - againstFree.EstimatedBytes, againstFree.HeadroomBytes,
            "headroom must be reported against the free-VRAM budget too.");
    }

    [Test]
    public void MemoryFit_GpuBudget_FallsBackToTotalVram_WhenFreeVramUnmeasured()
    {
        // Only NVIDIA reports free VRAM; every other vendor leaves it null, and a non-positive reading is not credible.
        // Neither may collapse the budget to zero and drop every model from the advisor.
        const long totalVram = 16 * Gb;
        const long paramCount = 8_000_000_000L;
        var estimator = new MemoryFitEstimator();

        var unmeasured = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount,
            CtxTarget, GpuProfile(totalVram), kvCacheQuantized: false);
        var zeroReading = estimator.Estimate("Q4_K_M", paramCount, fileSizeBytes: 0, BlockCount, KvHeads, EmbeddingLength, HeadCount,
            CtxTarget, GpuProfile(totalVram, availableVramBytes: 0), kvCacheQuantized: false);

        AssertEx.Equal(FitMode.Gpu, unmeasured.Mode);
        AssertEx.Equal(totalVram - unmeasured.EstimatedBytes, unmeasured.HeadroomBytes);
        AssertEx.Equal(unmeasured.HeadroomBytes, zeroReading.HeadroomBytes, "a zero free-VRAM reading falls back to total VRAM.");
        AssertEx.True(unmeasured.Fits, "an 8B Q4_K_M must still fit a 16 GiB card when free VRAM was not measured.");
    }

    [Test]
    public void FilterOutNativeFormatRequants_NoNativeFormat_ReturnsUnchanged()
    {
        var candidates = new[]
        {
            ("Q8_0", QuantLadder.QualityRank("Q8_0")),
            ("Q4_K_M", QuantLadder.QualityRank("Q4_K_M"))
        };

        var kept = MemoryFitEstimator.FilterOutNativeFormatRequants(candidates, candidate => candidate.Item1, candidate => candidate.Item2);

        AssertEx.Equal(expected: 2, kept.Count);
    }

    [Test]
    public void MemoryFit_MissingMetadata_FileSizeWeights_IsApproximate()
    {
        // No param count (weights fall back to file size) and no explicit key/value length ⇒ the estimate is a
        // conservative approximation, flagged so the advisor can present it as such.
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var estimate = estimator.Estimate("Q4_K_M", paramCount: null, 2 * Gb, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, kvCacheQuantized: false);

        AssertEx.Equal(FitConfidence.Approximate, estimate.Confidence);
    }

    // ---- S4: Multi-head Latent Attention (deepseek2), clamped conservative until measured ----

    // DeepSeek-V2-Lite shape, the worked example the plan pins: 27 layers, 16 kv-heads, explicit key/value 192/128,
    // key_length_mla = kv_lora_rank 512 + rope.dimension_count 64 = 576, value_length_mla = 512.
    private const long MlaBlockCount = 27L;
    private const long MlaKvHeads = 16L;
    private const long MlaKeyLength = 192L;
    private const long MlaValueLength = 128L;
    private const long MlaLatentKeyLength = 576L;
    private const long MlaLatentValueLength = 512L;
    private const long MlaCtx = 8192L;

    [Test]
    public void MlaBranch_NeverReturnsFewerBytesThanTheGenericFormula()
    {
        // The estimator sizes the VRAM admission ledger, so the MLA branch is clamped with max(mla, generic): it may
        // only ever RAISE an estimate. On the worked example the generic term (16 · (192+128) = 5120 B per layer per
        // token) dwarfs the MLA term (1 · 576 = 576 B), so the MLA file must estimate EXACTLY what it does today.
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var generic = new GgufAttentionShape(MlaKeyLength, MlaValueLength);
        var mla = new GgufAttentionShape(MlaKeyLength, MlaValueLength, SlidingWindow: null, SlidingWindowPattern: null,
            MlaLatentKeyLength, MlaLatentValueLength);
        AssertEx.False(generic.IsMla, "Without both *_mla lengths llama.cpp's is_mla() is false.");
        AssertEx.True(mla.IsMla, "Both *_mla lengths present and positive IS is_mla().");

        var withoutMla = EstimateMla(estimator, profile, generic);
        var withMla = EstimateMla(estimator, profile, mla);

        AssertEx.Equal(withoutMla.EstimatedBytes, withMla.EstimatedBytes);
        AssertEx.True(withMla.EstimatedBytes >= withoutMla.EstimatedBytes,
            "The MLA branch must never lower an estimate the admission ledger reserves against.");
    }

    [Test]
    public void MlaBranch_UnclampedFigure_MatchesTheWorkedExample()
    {
        // Two pins in one place, so Phase 2b (unclamp) is a one-line product change plus one expectation swap here.
        //   generic, per layer per token: 16 · (192 + 128) · 1 B (q8_0) = 5120 B  → · 27 · 8192 = 1_132_462_080 B
        //   MLA,     per layer per token:  1 ·  576        · 1 B (q8_0) =  576 B  → · 27 · 8192 =   127_401_984 B (8.9× lower)
        const long genericKvBytes = MlaKvHeads * (MlaKeyLength + MlaValueLength) * MlaBlockCount * MlaCtx;
        const long unclampedMlaKvBytes = MlaLatentKeyLength * MlaBlockCount * MlaCtx;
        AssertEx.Equal(expected: 1_132_462_080L, genericKvBytes);
        AssertEx.Equal(expected: 127_401_984L, unclampedMlaKvBytes);

        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();
        var mla = new GgufAttentionShape(MlaKeyLength, MlaValueLength, SlidingWindow: null, SlidingWindowPattern: null,
            MlaLatentKeyLength, MlaLatentValueLength);

        // What Phase 2 SHIPS: the clamped figure, i.e. the generic KV term, byte for byte.
        var weights = (long)(ParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var expected = weights + genericKvBytes + (long)((weights + genericKvBytes) * 0.12d) + MemoryFitEstimator.RuntimeOverheadBytes;
        AssertEx.Equal(expected, EstimateMla(estimator, profile, mla).EstimatedBytes);

        // The MLA arithmetic itself, exercised through the same code path by making the latent row the larger of the
        // two: a 16-byte generic head geometry puts the generic term below the latent one, so max() returns the latent
        // figure and pins `key_length_mla · 1 kv-head · bytes/element · layers · ctx`.
        var latentDominates = new GgufAttentionShape(KeyLength: 8, ValueLength: 8, SlidingWindow: null, SlidingWindowPattern: null,
            MlaLatentKeyLength, MlaLatentValueLength);
        var latentKv = MlaLatentKeyLength * MlaBlockCount * MlaCtx;
        var latentExpected = weights + latentKv + (long)((weights + latentKv) * 0.12d) + MemoryFitEstimator.RuntimeOverheadBytes;
        AssertEx.Equal(latentExpected, EstimateMla(estimator, profile, latentDominates).EstimatedBytes);
        AssertEx.Equal(unclampedMlaKvBytes, latentKv);
    }

    [Test]
    public void MlaBranch_WithNoComputableGenericTerm_ReturnsTheZeroEstimateNotTheMlaFigure()
    {
        // A deepseek2 file carrying key_length_mla but NO attention.key_length/value_length and no derivable head_dim
        // (embedding 0 / head count 0) has no generic term to clamp against. Clamping against an implicit zero would
        // ship the bare MLA under-estimate — the exact regression the clamp exists to prevent — so the MLA branch must
        // not apply at all and the KV term stays the existing zero estimate.
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();
        var mlaOnly = new GgufAttentionShape(KeyLength: null, ValueLength: null, SlidingWindow: null, SlidingWindowPattern: null,
            MlaLatentKeyLength, MlaLatentValueLength);

        var withMlaOnly = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, MlaBlockCount, MlaKvHeads,
            embeddingLength: 0, attentionHeadCount: 0, MlaCtx, profile, kvCacheQuantized: false,
            kvCacheQuant: KvCacheQuant.Q8_0, attention: mlaOnly);
        var noAttentionAtAll = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, MlaBlockCount, MlaKvHeads,
            embeddingLength: 0, attentionHeadCount: 0, MlaCtx, profile, kvCacheQuantized: false,
            kvCacheQuant: KvCacheQuant.Q8_0);

        var weights = (long)(ParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var weightsOnly = weights + (long)(weights * 0.12d) + MemoryFitEstimator.RuntimeOverheadBytes;
        AssertEx.Equal(weightsOnly, withMlaOnly.EstimatedBytes);
        AssertEx.Equal(noAttentionAtAll.EstimatedBytes, withMlaOnly.EstimatedBytes);
    }

    [Test]
    public void Estimate_WithoutMlaKeys_IsByteIdenticalToTheCurrentFormula()
    {
        // Byte-identical default: a non-MLA GGUF (both *_mla keys absent, which is every model but deepseek2) must be
        // sized by exactly the pre-slice formula. Recomputed here from first principles over the Qwen3 and Gemma3
        // fixtures, including the sliding-window layer accounting.
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        // Qwen3-style: explicit key/value 128, 28 layers, 8 kv-heads, ctx 4096, fp16 KV.
        const long qwenBlocks = 28L;
        const long qwenKvHeads = 8L;
        const long qwenCtx = 4096L;
        var qwen = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, qwenBlocks, qwenKvHeads, embeddingLength: 1024,
            attentionHeadCount: 32, qwenCtx, profile, kvCacheQuantized: false,
            attention: new GgufAttentionShape(KeyLength: 128, ValueLength: 128));
        var qwenWeights = (long)(ParamCount * MemoryFitEstimator.BytesPerWeight("Q4_K_M"));
        var qwenKv = (long)(qwenKvHeads * (128d + 128d) * 2d * (qwenBlocks * (double)qwenCtx));
        AssertEx.Equal(qwenWeights + qwenKv + (long)((qwenWeights + qwenKv) * 0.12d) + MemoryFitEstimator.RuntimeOverheadBytes,
            qwen.EstimatedBytes);

        // Gemma3-style SWA: 48 layers, key/value 256, window 1024, pattern 6 ⇒ 8 global + 40 window-limited layers.
        const long gemmaBlocks = 48L;
        const long gemmaKvHeads = 8L;
        const long gemmaCtx = 8192L;
        var gemma = estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, gemmaBlocks, gemmaKvHeads, embeddingLength: 0,
            attentionHeadCount: 0, gemmaCtx, profile, kvCacheQuantized: false,
            attention: new GgufAttentionShape(KeyLength: 256, ValueLength: 256, SlidingWindow: 1024, SlidingWindowPattern: 6));
        var gemmaTokens = (8L * (double)gemmaCtx) + (40L * 1024d);
        var gemmaKv = (long)(gemmaKvHeads * (256d + 256d) * 2d * gemmaTokens);
        AssertEx.Equal(qwenWeights + gemmaKv + (long)((qwenWeights + gemmaKv) * 0.12d) + MemoryFitEstimator.RuntimeOverheadBytes,
            gemma.EstimatedBytes);
    }

    private static MemoryFitEstimate EstimateMla(MemoryFitEstimator estimator, HardwareProfile profile, GgufAttentionShape attention)
    {
        return estimator.Estimate("Q4_K_M", ParamCount, fileSizeBytes: 0, MlaBlockCount, MlaKvHeads, embeddingLength: 0,
            attentionHeadCount: 0, MlaCtx, profile, kvCacheQuantized: false, kvCacheQuant: KvCacheQuant.Q8_0, attention: attention);
    }

    private static HardwareProfile GpuProfile(long vramBytes, long? availableVramBytes = null)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }
}
