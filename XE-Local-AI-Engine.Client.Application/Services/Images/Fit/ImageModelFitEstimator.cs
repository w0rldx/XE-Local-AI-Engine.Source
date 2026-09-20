namespace XE_Local_AI_Engine.Client.Services.Images.Fit;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>How a diffusion file-set compares to this box's memory budget.</summary>
public enum ImageModelFitVerdict
{
    /// <summary>The budget could not be measured, so no claim is made.</summary>
    /// <remarks>
    ///     A first-class outcome, NOT a soft "probably fine": <c>HardwareProfiler</c> leaves VRAM unmeasured on every
    ///     non-NVIDIA GPU, and rendering that as "Fits" would promise a 13 GB install will run on a box nobody probed.
    /// </remarks>
    Unknown = 0,

    /// <summary>Comfortably inside the budget.</summary>
    Fits = 1,

    /// <summary>Inside the budget but with little headroom — expect swapping/offload pressure.</summary>
    Tight = 2,

    /// <summary>Larger than the budget.</summary>
    WontFit = 3
}

/// <summary>
///     The fit verdict for one image model plus the numbers behind it, so the UI can explain itself rather than showing
///     a bare badge. <see cref="ResidentBytes" /> is what actually has to be resident (see
///     <see cref="ImageModelFitEstimator" />), <see cref="TotalBytes" /> is the whole download.
/// </summary>
public sealed class ImageModelFitEstimate
{
    public required ImageModelFitVerdict Verdict { get; init; }

    public required long ResidentBytes { get; init; }

    public required long TotalBytes { get; init; }

    public required long BudgetBytes { get; init; }

    public required bool FitsOnDisk { get; init; }
}

/// <summary>
///     One file of a diffusion set: the role it plays at run time (which decides whether it is charged against VRAM)
///     and its size on disk.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ImageModelPartSize(ImageModelPartRole Role, long SizeBytes);

/// <summary>
///     Scores a diffusion file-set against the host's memory budget.
/// </summary>
/// <remarks>
///     Deliberately NOT <c>MemoryFitEstimator.Estimate</c>: that estimator's model is a transformer LLM's and a GGUF
///     diffusion file exposes none of the fields it needs. What reuses is the hardware probe —
///     <see cref="MemoryFitEstimator.ResolveFitBudgetBytes" /> — so an image verdict is scored against the identical
///     budget the LLM advisor uses. Only the diffusion part is a VRAM cost: <c>ImageServerArgumentBuilder.BuildBackendSpec</c>
///     pins the text encoder and VAE to the CPU on every GPU backend. See docs/wiki/14-image-generation.md ("Model fit for a diffusion set").
/// </remarks>
public static class ImageModelFitEstimator
{
    // Fraction of the budget below which a set is called a comfortable fit rather than tight. The remaining headroom
    // absorbs the runtime's own allocations and the working buffers a diffusion step needs beyond the weights.
    private const double ComfortableFraction = 0.8;

    /// <summary>
    ///     Scores a file-set whose parts are (role, size) pairs. <paramref name="profile" /> may be
    ///     <see langword="null" /> when the hardware probe itself failed, which yields
    ///     <see cref="ImageModelFitVerdict.Unknown" />.
    /// </summary>
    public static ImageModelFitEstimate Estimate(IReadOnlyList<ImageModelPartSize> parts, HardwareProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var totalBytes = parts.Sum(static part => Math.Max(val1: 0L, part.SizeBytes));
        var diffusionBytes = parts.Where(static part => part.Role == ImageModelPartRole.Diffusion)
                                  .Sum(static part => Math.Max(val1: 0L, part.SizeBytes));

        if (profile is null)
        {
            return new ImageModelFitEstimate { Verdict = ImageModelFitVerdict.Unknown, ResidentBytes = diffusionBytes, TotalBytes = totalBytes, BudgetBytes = 0, FitsOnDisk = true };
        }

        var fitsOnDisk = profile.FreeDiskBytes <= 0 || profile.FreeDiskBytes >= totalBytes;

        // A GPU is present but its VRAM was never measured (every non-NVIDIA vendor, and NVIDIA without nvidia-smi). There is no budget to score against and the
        // CPU budget is the wrong one — the box would run on the GPU. Say so instead of guessing in either direction.
        if (profile.GpuVendor is not GpuVendor.None && !profile.VramKnown)
        {
            return new ImageModelFitEstimate { Verdict = ImageModelFitVerdict.Unknown, ResidentBytes = diffusionBytes, TotalBytes = totalBytes, BudgetBytes = 0, FitsOnDisk = fitsOnDisk };
        }

        var budgetBytes = MemoryFitEstimator.ResolveFitBudgetBytes(profile);
        if (budgetBytes <= 0)
        {
            return new ImageModelFitEstimate { Verdict = ImageModelFitVerdict.Unknown, ResidentBytes = diffusionBytes, TotalBytes = totalBytes, BudgetBytes = 0, FitsOnDisk = fitsOnDisk };
        }

        // GPU mode charges only the diffusion transformer (encoders + VAE are pinned to CPU); CPU mode charges the set.
        var residentBytes = profile.GpuAccelAvailable ? diffusionBytes : totalBytes;

        return new ImageModelFitEstimate { Verdict = ResolveVerdict(residentBytes, budgetBytes), ResidentBytes = residentBytes, TotalBytes = totalBytes, BudgetBytes = budgetBytes, FitsOnDisk = fitsOnDisk };
    }

    private static ImageModelFitVerdict ResolveVerdict(long residentBytes, long budgetBytes)
    {
        if (residentBytes <= (long)(budgetBytes * ComfortableFraction))
        {
            return ImageModelFitVerdict.Fits;
        }

        return residentBytes <= budgetBytes ? ImageModelFitVerdict.Tight : ImageModelFitVerdict.WontFit;
    }
}
