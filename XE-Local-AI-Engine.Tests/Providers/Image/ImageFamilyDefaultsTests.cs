namespace XE_Local_AI_Engine.Tests.Providers.Image;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the per-family generation defaults. They matter because the wrong ones do not fail: <c>sd-server</c>'s own
///     defaults are SD1.5's (20 steps, CFG 7.0, <c>euler_a</c>), and running a distilled FLUX-schnell or a Qwen-Image at
///     those numbers produces a burnt image rather than an error — a failure mode that looks like a broken model.
/// </summary>
public sealed class ImageFamilyDefaultsTests
{
    [Test]
    [Arguments(ImageModelFamily.Sd15, 20, 7.0, "euler_a")]
    [Arguments(ImageModelFamily.Sdxl, 25, 7.0, "euler_a")]
    [Arguments(ImageModelFamily.Sd3, 28, 4.5, "euler")]
    [Arguments(ImageModelFamily.Flux, 4, 1.0, "euler")]
    [Arguments(ImageModelFamily.QwenImage, 20, 2.5, "euler")]
    public void For_ReturnsTheFamilysStartingParameters(ImageModelFamily family, int steps, double cfgScale, string sampler)
    {
        var defaults = ImageFamilyDefaults.For(family);

        AssertEx.Equal(steps, defaults.Steps);
        AssertEx.Equal(cfgScale, defaults.CfgScale);
        AssertEx.Equal(sampler, defaults.Sampler);
    }

    [Test]
    public void For_UnknownFamily_FallsBackToTheSdEraValuesRatherThanThrowing()
    {
        // An unclassified model still has to be generatable. The fallback is what the runtime would have used anyway,
        // so the worst case is the previous behaviour rather than an unusable model.
        var defaults = ImageFamilyDefaults.For(ImageModelFamily.Unknown);

        AssertEx.Equal(expected: 20, defaults.Steps);
        AssertEx.Equal(expected: 7.0, defaults.CfgScale);
        AssertEx.Equal("euler_a", defaults.Sampler);
    }

    [Test]
    public void For_EveryDeclaredFamily_ProducesUsableParameters()
    {
        // Guards the switch against a family being added to the enum and silently inheriting the SD1.5 fallback with
        // nobody deciding that was right: every value must at least be inside the request contract's own bounds.
        foreach (var family in Enum.GetValues<ImageModelFamily>())
        {
            var defaults = ImageFamilyDefaults.For(family);
            AssertEx.True(defaults.Steps is >= 1 and <= 150, $"{family} step count is outside the request contract's bounds.");
            AssertEx.True(defaults.CfgScale is >= 1.0 and <= 30.0, $"{family} CFG scale is outside the request contract's bounds.");
            AssertEx.False(string.IsNullOrWhiteSpace(defaults.Sampler), $"{family} must name a sampler.");
        }
    }
}
