namespace XE_Local_AI_Engine.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Strict legacy CUDA compatibility predicate. It recognizes an <c>installed-runtime.json</c> record written by the
///     original pre-generalization CUDA adopt path — one whose source-provenance fields are all null — so startup
///     recovery can re-validate and keep it instead of stranding an adopted build after an upgrade.
/// </summary>
public static class LlamaCppSourceBuildCompatibility
{
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
