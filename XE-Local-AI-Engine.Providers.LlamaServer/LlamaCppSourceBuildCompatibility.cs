namespace XE_Local_AI_Engine.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Strict legacy CUDA compatibility predicates.</summary>
public static class LlamaCppSourceBuildCompatibility
{
    public static bool IsLegacyPinnedCuda(this LlamaCppSourceBuildDescriptor? descriptor)
    {
        return descriptor is
        {
            Variant: GpuVariant.Cuda,
            Source: LlamaCppSourceSelection.Official,
            RevisionMode: LlamaCppSourceRevisionMode.EnginePinned,
            RequestedCommit: null
        }
        && string.Equals(descriptor.Repository, LlamaCppSourceBuildRequestValidation.OfficialRepository, StringComparison.Ordinal)
        && string.Equals(descriptor.ResolvedCommit, LlamaCppReleasePins.PinnedSourceCommitSha, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLegacyPinnedCuda(this InstalledRuntimeState? state, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (state is null || state.Variant != GpuVariant.Cuda || state.SourceBuildPath is not { Length: > 0 })
        {
            return false;
        }

        if (state.SourceRepository is null
            && state.SourceCommit is null
            && state.SourceRevisionMode is null
            && state.SourceRequestedCommit is null
            && state.SourceSelection is null)
        {
            try
            {
                var expected = Path.GetFullPath(Path.Combine(cacheRoot,
                    "llama.cpp",
                    "source-cuda",
                    LlamaCppReleasePins.PinnedTag,
                    "build",
                    "bin"));
                var recorded = Path.GetFullPath(state.SourceBuildPath);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return string.Equals(state.Tag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal)
                    && string.Equals(recorded, expected, comparison);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        return state.SourceRevisionMode == LlamaCppSourceRevisionMode.EnginePinned
            && state.SourceRequestedCommit is null
            && state.SourceSelection is not LlamaCppSourceSelection.Custom
            && string.Equals(state.SourceRepository, LlamaCppSourceBuildRequestValidation.OfficialRepository, StringComparison.Ordinal)
            && string.Equals(state.SourceCommit, LlamaCppReleasePins.PinnedSourceCommitSha, StringComparison.OrdinalIgnoreCase);
    }
}
