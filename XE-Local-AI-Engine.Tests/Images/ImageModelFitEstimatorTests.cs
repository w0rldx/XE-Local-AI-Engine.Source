namespace XE_Local_AI_Engine.Tests.Images;

using XE_Local_AI_Engine.Client.Services.Images.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The image fit annotation exists so a user is not offered an 18 GB one-click install their box cannot run. Three
///     behaviours carry that promise and each has a specific way of going wrong: only the diffusion part is charged
///     against VRAM (the runtime pins the text encoder and VAE to CPU, so charging the whole set would reject models
///     that run fine), the whole set is charged in CPU mode, and an unmeasured budget reports <c>Unknown</c> rather
///     than degrading to a comfortable-sounding "Fits".
/// </summary>
public sealed class ImageModelFitEstimatorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Test]
    public void Estimate_OnGpu_ChargesOnlyTheDiffusionPart()
    {
        // A Qwen-Image-shaped set: 13 GB diffusion + a small VAE + a 4.7 GB text encoder. The encoder and VAE run on
        // CPU (ImageServerArgumentBuilder emits diffusion=cuda0,te=cpu,vae=cpu), so a 16 GB card fits this set even
        // though the set totals ~18 GB. Charging the total would wrongly report WontFit.
        var parts = new[]
        {
            (ImageModelPartRole.Diffusion, 13 * Gb),
            (ImageModelPartRole.Vae, Gb / 4),
            (ImageModelPartRole.Llm, 5 * Gb)
        };

        var estimate = ImageModelFitEstimator.Estimate(parts, NvidiaProfile(freeVramBytes: 24 * Gb));

        AssertEx.Equal(ImageModelFitVerdict.Fits, estimate.Verdict);
        AssertEx.Equal(13 * Gb, estimate.ResidentBytes, "Only the diffusion transformer is resident in VRAM.");
        AssertEx.True(estimate.TotalBytes > estimate.ResidentBytes, "The download is still the whole set.");
    }

    [Test]
    public void Estimate_OnGpu_WhenTheDiffusionPartAloneExceedsVram_ReportsWontFit()
    {
        var parts = new[]
        {
            (ImageModelPartRole.Diffusion, 13 * Gb)
        };

        var estimate = ImageModelFitEstimator.Estimate(parts, NvidiaProfile(freeVramBytes: 8 * Gb));

        AssertEx.Equal(ImageModelFitVerdict.WontFit, estimate.Verdict);
    }

    [Test]
    public void Estimate_OnGpu_JustInsideTheBudget_ReportsTight()
    {
        // Inside the budget but past the comfortable fraction: the operator is told it will run, and that it will be
        // close, rather than being given a flat green badge that hides the risk.
        var parts = new[]
        {
            (ImageModelPartRole.Diffusion, 7 * Gb)
        };

        var estimate = ImageModelFitEstimator.Estimate(parts, NvidiaProfile(freeVramBytes: 8 * Gb));

        AssertEx.Equal(ImageModelFitVerdict.Tight, estimate.Verdict);
    }

    [Test]
    public void Estimate_OnACpuOnlyBox_ChargesTheWholeSetAgainstAvailableRam()
    {
        // There is no diffusion/encoder split without a GPU: everything is resident in RAM.
        var parts = new[]
        {
            (ImageModelPartRole.Diffusion, 6 * Gb),
            (ImageModelPartRole.T5, 3 * Gb)
        };

        var estimate = ImageModelFitEstimator.Estimate(parts, CpuProfile(availableRamBytes: 10 * Gb));

        AssertEx.Equal(9 * Gb, estimate.ResidentBytes);
        AssertEx.Equal(ImageModelFitVerdict.Tight, estimate.Verdict);
    }

    [Test]
    public void Estimate_WhenAGpuIsPresentButItsVramWasNeverMeasured_ReportsUnknown()
    {
        // HardwareProfiler measures VRAM through nvidia-smi only, so every AMD/Intel box lands here. Unknown must NOT
        // degrade to Fits: the alternative is promising a 13 GB install will run on a box nobody probed.
        var profile = new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = null,
            AvailableVramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Amd,
            GpuAccelAvailable = false,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };

        var estimate = ImageModelFitEstimator.Estimate([(ImageModelPartRole.Diffusion, Gb)], profile);

        AssertEx.Equal(ImageModelFitVerdict.Unknown, estimate.Verdict);
        AssertEx.Equal(expected: 0L, estimate.BudgetBytes, "No budget was measured, so none may be reported.");
    }

    [Test]
    public void Estimate_WhenTheHardwareProbeItselfFailed_ReportsUnknown()
    {
        var estimate = ImageModelFitEstimator.Estimate([(ImageModelPartRole.Diffusion, Gb)], profile: null);

        AssertEx.Equal(ImageModelFitVerdict.Unknown, estimate.Verdict);
    }

    [Test]
    public void Estimate_FlagsASetLargerThanTheFreeDisk()
    {
        // Independent of the memory verdict: a set can fit in VRAM and still not fit on the volume it downloads to.
        var profile = NvidiaProfile(freeVramBytes: 24 * Gb) with
        {
            FreeDiskBytes = 5 * Gb
        };

        var estimate = ImageModelFitEstimator.Estimate([(ImageModelPartRole.Diffusion, 13 * Gb)], profile);

        AssertEx.Equal(ImageModelFitVerdict.Fits, estimate.Verdict);
        AssertEx.False(estimate.FitsOnDisk);
    }

    private static HardwareProfile NvidiaProfile(long freeVramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = freeVramBytes,
            AvailableVramBytes = freeVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    private static HardwareProfile CpuProfile(long availableRamBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = availableRamBytes * 2,
            AvailableRamBytes = availableRamBytes,
            VramBytes = null,
            AvailableVramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.None,
            GpuAccelAvailable = false,
            CpuCores = 8,
            FreeDiskBytes = 500 * Gb
        };
    }
}
