namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Per-family generation defaults — the sampling parameters a diffusion family is actually meant to run at.
/// </summary>
/// <remarks>
///     <c>sd-server</c>'s own defaults (20 steps, CFG 7.0, <c>euler_a</c>) are the <b>SD1.5</b> defaults, and applying them to a later
///     family produces a bad image rather than an error: FLUX-schnell is distilled for ~4 steps at CFG 1.0 and burns out at 7.0, and
///     Qwen-Image 2.1 is meant for 40 steps at CFG 6.0, so a form pre-filling SD-era numbers for every model looks like it works
///     and quietly generates garbage. These are <em>starting points</em> surfaced to the operator, never a clamp: the request contract
///     keeps its own bounds and the operator can override any of them.
/// </remarks>
public static class ImageFamilyDefaults
{
    private static readonly IReadOnlyList<ImageEditMode> Img2ImgOnly = [ImageEditMode.Img2Img];

    /// <summary>The fallback used for <see cref="ImageModelFamily.Unknown" /> and any family not listed — SD1.5-era values.</summary>
    private static readonly ImageGenerationDefaults Fallback = new()
    {
        Steps = 20,
        CfgScale = 7.0,
        Sampler = "euler_a",
        EditModes = Img2ImgOnly,
        NativePixels = 512 * 512
    };

    /// <summary>
    ///     The recommended starting parameters for <paramref name="family" />. Never returns <see langword="null" /> — an
    ///     unrecognized family falls back to the SD1.5-era values, which is what the runtime would have used anyway.
    /// </summary>
    public static ImageGenerationDefaults For(ImageModelFamily family)
    {
        return family switch
        {
            // SDXL keeps the SD sampler but is normally run a little longer than SD1.5.
            ImageModelFamily.Sdxl => new ImageGenerationDefaults
            {
                Steps = 25,
                CfgScale = 7.0,
                Sampler = "euler_a",
                EditModes = Img2ImgOnly,
                NativePixels = 1024 * 1024
            },

            // Flow-matching families default to plain euler in sd.cpp and want markedly lower guidance than SD.
            ImageModelFamily.Sd3 => new ImageGenerationDefaults
            {
                Steps = 28,
                CfgScale = 4.5,
                Sampler = "euler",
                EditModes = Img2ImgOnly,
                NativePixels = 1024 * 1024
            },

            // FLUX.1-schnell is timestep-distilled: it is meant to run at ~4 steps with guidance effectively disabled
            // (CFG 1.0). Running it at 20/7.0 is both ~5x slower and visibly worse.
            ImageModelFamily.Flux => new ImageGenerationDefaults
            {
                Steps = 4,
                CfgScale = 1.0,
                Sampler = "euler",
                EditModes = Img2ImgOnly,
                NativePixels = 1024 * 1024
            },

            // Qwen-Image 2.1 (the curated set): CFG 6.0 + euler from sd.cpp docs/qwen_image_2.1.md, 40 steps from the
            // QwenLM model card. The original Qwen-Image wanted CFG 2.5; the operator overrides it for such an import.
            ImageModelFamily.QwenImage => new ImageGenerationDefaults
            {
                Steps = 40,
                CfgScale = 6.0,
                Sampler = "euler",

                // Reference is the family's ceiling, not a promise: an install only gets it with the vision tower, see
                // EditModesFor.
                EditModes = [ImageEditMode.Img2Img, ImageEditMode.Reference],
                NativePixels = 1024 * 1024
            },

            _ => Fallback
        };
    }

    /// <summary>
    ///     The edit modes one <b>installed</b> model offers: its family's <see cref="ImageGenerationDefaults.EditModes" />,
    ///     minus <see cref="ImageEditMode.Reference" /> for a Qwen-Image set that carries no
    ///     <see cref="ImageModelPartRole.LlmVision" /> part.
    /// </summary>
    /// <remarks>
    ///     Qwen-Image conditions a reference edit on the source through the Qwen-VL vision tower (<c>--llm_vision</c>).
    ///     Without it sd-server fails every reference job ("Qwen Image 2.1 editing requires Qwen3-VL vision weights"),
    ///     so offering the mode on such an install would offer a mode that always fails.
    /// </remarks>
    public static IReadOnlyList<ImageEditMode> EditModesFor(ImageModelFamily family, IEnumerable<ImageModelPartRole> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var modes = For(family).EditModes;
        if (family == ImageModelFamily.QwenImage && !parts.Contains(ImageModelPartRole.LlmVision))
        {
            return [.. modes.Where(static m => m != ImageEditMode.Reference)];
        }

        return modes;
    }
}

/// <summary>Recommended starting generation parameters for one diffusion family. See <see cref="ImageFamilyDefaults" />.</summary>
public sealed record ImageGenerationDefaults
{
    /// <summary>Recommended number of sampling steps.</summary>
    public required int Steps { get; init; }

    /// <summary>Recommended classifier-free-guidance scale.</summary>
    public required double CfgScale { get; init; }

    /// <summary>Recommended sd-server sampling method name.</summary>
    public required string Sampler { get; init; }

    /// <summary>
    ///     The edit modes this family can offer (per-model quality, not the runtime build's feature flags). An install
    ///     may offer fewer; use <see cref="ImageFamilyDefaults.EditModesFor" />.
    /// </summary>
    public required IReadOnlyList<ImageEditMode> EditModes { get; init; }

    /// <summary>The family-native training resolution as a pixel count (width x height), the target an edit's output is fitted to.</summary>
    public required int NativePixels { get; init; }
}
