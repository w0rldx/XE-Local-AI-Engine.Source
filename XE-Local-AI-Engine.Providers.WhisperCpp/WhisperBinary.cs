namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     A resolved whisper.cpp <c>whisper-server</c> executable on disk, together with what it is.
/// </summary>
/// <param name="ServerExecutablePath">Absolute path to the resolved <c>whisper-server</c> executable.</param>
/// <param name="Version">
///     The pinned release tag, the managed build's source commit, or <c>byo</c> for an operator-supplied binary.
/// </param>
/// <param name="Backend">The acceleration backend the resolved binary was built for.</param>
/// <param name="IsPinnedFallback">
///     <see langword="true" /> when this is the pinned prebuilt; <see langword="false" /> for a managed source build or
///     an operator bring-your-own override.
/// </param>
public sealed record WhisperBinary(
    string ServerExecutablePath,
    string Version,
    WhisperBackend Backend,
    bool IsPinnedFallback);
