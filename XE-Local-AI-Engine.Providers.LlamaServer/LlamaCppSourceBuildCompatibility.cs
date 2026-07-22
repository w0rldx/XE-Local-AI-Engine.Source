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

    public static bool IsLegacyPinnedCuda(this InstalledRuntimeState? state)
    {
        if (state is null || state.Variant != GpuVariant.Cuda || state.SourceBuildPath is not { Length: > 0 })
        {
            return false;
        }

        if (state.SourceRepository is null && state.SourceCommit is null && state.SourceRevisionMode is null && state.SourceRequestedCommit is null)
        {
            var legacyRoot = Path.Combine("llama.cpp", "source-cuda") + Path.DirectorySeparatorChar;
            return string.Equals(state.Tag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal)
                && state.SourceBuildPath.Contains(legacyRoot, StringComparison.Ordinal);
        }

        return state.SourceRevisionMode == LlamaCppSourceRevisionMode.EnginePinned
            && state.SourceRequestedCommit is null
            && string.Equals(state.SourceRepository, LlamaCppSourceBuildRequestValidation.OfficialRepository, StringComparison.Ordinal)
            && string.Equals(state.SourceCommit, LlamaCppReleasePins.PinnedSourceCommitSha, StringComparison.OrdinalIgnoreCase);
    }
}
