namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The managed stable-diffusion.cpp runtime record, under <c>{cacheRoot}/stable-diffusion.cpp/</c>.</summary>
public sealed class StableDiffusionInstalledRuntimeStore : ManagedRuntimeStore<SdGpuBackend>
{
    public StableDiffusionInstalledRuntimeStore(string? cacheRoot = null)
        : base(cacheRoot,
            "stable-diffusion.cpp",
            StableDiffusionCppSourceBuildRequestValidation.OfficialRepository,
            StableDiffusionReleasePins.PinnedSourceCommitSha)
    {
    }

    protected override bool IsCanonicalGitHubRepository(string value)
    {
        try
        {
            return string.Equals(StableDiffusionCppSourceBuildRequestValidation.NormalizeGitHubRepository(value),
                value,
                StringComparison.Ordinal);
        }
        catch (StableDiffusionRuntimeException)
        {
            return false;
        }
    }
}
