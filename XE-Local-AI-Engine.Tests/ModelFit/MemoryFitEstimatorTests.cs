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
            0,
            BlockCount,
            KvHeads,
            EmbeddingLength,
            HeadCount,
            CtxTarget,
            profile,
            false);

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

        var tooBig = estimator.Estimate("Q4_K_M", 70_000_000_000L, 0, 80, 8, 8192, 64, CtxTarget, tightProfile, false);
        var fits = estimator.Estimate("Q4_K_M", 70_000_000_000L, 0, 80, 8, 8192, 64, CtxTarget, roomyProfile, false);

        AssertEx.False(tooBig.Fits, "a 70B model must not fit a 4 GB VRAM budget.");
        AssertEx.True(tooBig.HeadroomBytes < 0, "headroom must be negative when the model exceeds the budget.");
        AssertEx.True(fits.Fits, "the same 70B model must fit a 64 GB budget.");
    }

    [Test]
    public void MemoryFit_KvCacheQuant_LowersKvTerm()
    {
        var profile = GpuProfile(64 * Gb);
        var estimator = new MemoryFitEstimator();

        var fp16 = estimator.Estimate("Q4_K_M", ParamCount, 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, false);
        var quantized = estimator.Estimate("Q4_K_M", ParamCount, 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, profile, true);

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

        var estimate = estimator.Estimate("Q4_K_M", ParamCount, 0, BlockCount, KvHeads, EmbeddingLength, HeadCount, CtxTarget, cpuProfile, false);

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
            null,
            2 * Gb,
            BlockCount,
            KvHeads,
            EmbeddingLength,
            HeadCount,
            CtxTarget,
            profile,
            false);

        AssertEx.True(estimate.EstimatedBytes > 2 * Gb, "the weights fallback must use the on-disk file size.");
        AssertEx.True(estimate.Fits);
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
