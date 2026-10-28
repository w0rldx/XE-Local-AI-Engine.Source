namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The managed whisper.cpp runtime record, under <c>{cacheRoot}/whisper.cpp/</c>.</summary>
public sealed class WhisperInstalledRuntimeStore : ManagedRuntimeStore<WhisperBackend>
{
    public WhisperInstalledRuntimeStore(string? cacheRoot = null)
        : base(cacheRoot,
            "whisper.cpp",
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppReleasePins.PinnedSourceCommitSha)
    {
    }

    protected override bool IsCanonicalGitHubRepository(string value)
    {
        try
        {
            return string.Equals(WhisperCppSourceBuildRequestValidation.NormalizeGitHubRepository(value),
                value,
                StringComparison.Ordinal);
        }
        catch (WhisperRuntimeException)
        {
            return false;
        }
    }
}
