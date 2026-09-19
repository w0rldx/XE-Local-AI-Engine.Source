namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     A resolved whisper.cpp <c>whisper-server</c> executable on disk, together with what it is.
/// </summary>
public sealed class WhisperBinary
{
    /// <summary>Absolute path to the resolved <c>whisper-server</c> executable.</summary>
    public required string ServerExecutablePath { get; init; }

    /// <summary>The pinned release tag, the managed build's source commit, or <c>byo</c> for an operator-supplied binary.</summary>
    public required string Version { get; init; }

    /// <summary>The acceleration backend the resolved binary was built for.</summary>
    public required WhisperBackend Backend { get; init; }

    /// <summary>
    ///     <see langword="true" /> when this is the pinned prebuilt; <see langword="false" /> for a managed source build or
    ///     an operator bring-your-own override.
    /// </summary>
    public required bool IsPinnedFallback { get; init; }
}
