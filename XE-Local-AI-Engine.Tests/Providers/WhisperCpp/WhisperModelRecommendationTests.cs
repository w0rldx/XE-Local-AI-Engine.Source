namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Brief test 6. The recommendation is a pure function over a <see cref="HardwareProfile" />, so every case here is
///     a literal profile and an expected catalogue row — no hardware, no probe, no host.
///     <para>
///         The GPU table deliberately pins the two boundaries INSIDE <see cref="WhisperModelTier.LargeTurbo" />, not
///         only the tier jumps: three rows share that tier, so a rule written against tiers rather than rows would pass
///         a tier-only table while recommending a different model than this one does.
///     </para>
/// </summary>
public sealed class WhisperModelRecommendationTests
{
    private const long Gigabyte = 1024L * 1024L * 1024L;

    [Test]
    // 2 GiB: q5_0 needs 1.5 GiB x 1.25 = 1.875 GiB and fits; q8_0 needs 1.8 GiB x 1.25 = 2.25 GiB and does not.
    [Arguments(2L, "large-v3-turbo-q5_0")]
    // 3 GiB: q8_0's 2.25 GiB fits; the full turbo needs 2.6 GiB x 1.25 = 3.25 GiB and does not.
    [Arguments(3L, "large-v3-turbo-q8_0")]
    // 4 GiB: the full turbo's 3.25 GiB fits.
    [Arguments(4L, "large-v3-turbo")]
    // This box (a 32 GiB card): still the full turbo, because nothing larger exists in the catalogue.
    [Arguments(28L, "large-v3-turbo")]
    public void Recommend_GpuBudgets_ReturnExpectedRow(long vramGigabytes, string expectedModelId)
    {
        var profile = GpuProfile(vramGigabytes * Gigabyte);

        var recommended = WhisperModelRecommendation.Recommend(profile, WhisperBackend.Cuda);

        AssertEx.Equal(expectedModelId, recommended.Id,
            $"A {vramGigabytes} GiB VRAM budget must resolve to '{expectedModelId}' under the 1.25x headroom rule.");
    }

    [Test]
    public void Recommend_CpuBackend_NeverExceedsSmall()
    {
        var profile = CpuProfile(8 * Gigabyte);

        var recommended = WhisperModelRecommendation.Recommend(profile, WhisperBackend.Cpu);

        AssertEx.True(recommended.Tier <= WhisperModelTier.Small,
            $"A CPU backend must never be recommended above the Small tier; got '{recommended.Id}' ({recommended.Tier}).");
    }

    [Test]
    public void Recommend_CpuBackendWithAHugeRamBudget_StillReturnsSmall()
    {
        // The CPU ceiling is a TIER check, not a budget check: a 64 GB box must not be handed a turbo row, because a
        // large model on CPU is slower than real time however much memory is free.
        var profile = CpuProfile(64 * Gigabyte);

        var recommended = WhisperModelRecommendation.Recommend(profile, WhisperBackend.Cpu);

        AssertEx.Equal("small", recommended.Id);
    }

    [Test]
    public void Recommend_NoBudgetFits_ReturnsTiny()
    {
        // Below even the entry-level row's headroom. The answer is the smallest model, never null: a node that cannot
        // comfortably run anything is still offered the one thing it has the best chance with.
        var profile = GpuProfile(64L * 1024 * 1024);

        var recommended = WhisperModelRecommendation.Recommend(profile, WhisperBackend.Cuda);

        AssertEx.Equal("tiny", recommended.Id);
    }

    [Test]
    public void Recommend_VramUnknownOnAGpuBackend_FallsBackToRamBudget()
    {
        // An unmeasurable device is not evidence of a large one. With VRAM unknown the rule sizes against RAM — both
        // the budget AND the footprint column, so a VRAM figure is never weighed against a RAM budget.
        var profile = new HardwareProfile
        {
            TotalRamBytes = 4 * Gigabyte,
            AvailableRamBytes = 2 * Gigabyte,
            VramBytes = null,
            AvailableVramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = false,
            CpuCores = 16,
            FreeDiskBytes = 200 * Gigabyte
        };

        var recommended = WhisperModelRecommendation.Recommend(profile, WhisperBackend.Cuda);

        AssertEx.Equal("large-v3-turbo-q8_0", recommended.Id,
            "With VRAM unknown the GPU budget must come from RAM against the RAM column, not from a guess at the device size.");
    }

    [Test]
    public void Recommend_GpuBackend_PrefersFreeVramOverTotal()
    {
        // A card with 32 GiB total but 2 GiB free must be sized by what is actually free: the other 30 GiB belong to a
        // resident chat model.
        var profile = GpuProfile(2 * Gigabyte);

        var recommended = WhisperModelRecommendation.Recommend(profile, WhisperBackend.Cuda);

        AssertEx.Equal("large-v3-turbo-q5_0", recommended.Id);
    }

    private static HardwareProfile GpuProfile(long availableVramBytes) =>
        new()
        {
            TotalRamBytes = 47 * Gigabyte,
            AvailableRamBytes = 32 * Gigabyte,
            VramBytes = 32 * Gigabyte,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 200 * Gigabyte
        };

    private static HardwareProfile CpuProfile(long availableRamBytes) =>
        new()
        {
            TotalRamBytes = availableRamBytes,
            AvailableRamBytes = availableRamBytes,
            VramBytes = null,
            AvailableVramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.None,
            GpuAccelAvailable = false,
            CpuCores = 8,
            FreeDiskBytes = 200 * Gigabyte
        };
}
