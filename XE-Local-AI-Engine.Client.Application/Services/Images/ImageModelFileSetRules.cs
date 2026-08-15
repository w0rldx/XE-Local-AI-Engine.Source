namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Shape rules for an image-model file-set, enforced before a multi-gigabyte download is started. Both rules are
///     dictated by the launch-argument builder (<c>ImageServerArgumentBuilder</c>): it emits ONE flag per role
///     (<c>--diffusion-model</c>, <c>--vae</c>, <c>--clip_l</c>, …) and iterates the whole set, so a set with no
///     diffusion part cannot start and a second file for a role would either pass its flag twice or be downloaded and
///     then never referenced. Cheap to type by hand and easy to click twice in the repo file picker, so it is rejected
///     at the boundary rather than surfacing as a model the runtime cannot start.
/// </summary>
public static class ImageModelFileSetRules
{
    /// <summary>
    ///     Returns the first violation's operator-facing message, or <see langword="null" /> when the set is usable.
    /// </summary>
    public static string? Validate(IReadOnlyList<ImageModelPartRequest> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.All(static part => part.Role != ImageModelPartRole.Diffusion))
        {
            return "The file-set must include a diffusion part.";
        }

        var duplicateRole = parts.GroupBy(static part => part.Role).FirstOrDefault(static group => group.Count() > 1);

        return duplicateRole is null ? null : $"The file-set declares the '{duplicateRole.Key}' part more than once.";
    }
}
