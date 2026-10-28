namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     An operator's request to compile a managed whisper.cpp runtime from source.
/// </summary>
/// <remarks>
///     This record ships on its own rather than with the rest of the source-build contract surface:
///     <see cref="WhisperCppSourceBuildRequestValidation.Normalize" /> takes it, and the installed-runtime store validates
///     a stored repository by round-tripping it through the same normalizer.
/// </remarks>
public sealed record WhisperCppSourceBuildRequest
{
    /// <summary>The acceleration backend to compile for.</summary>
    public required WhisperBackend Backend { get; init; }

    /// <summary>Official or operator-supplied repository.</summary>
    public required ManagedSourceSelection Source { get; init; }

    /// <summary>The repository URL; ignored (and pinned by the server) for the official source.</summary>
    public string? Repository { get; init; }

    /// <summary>The requested commit; rejected for the official source, which uses the engine-pinned revision.</summary>
    public string? Commit { get; init; }

    /// <summary>
    ///     Operator acknowledgement that a custom repository's build scripts execute with the app user's privileges.
    ///     Required for <see cref="ManagedSourceSelection.Custom" />.
    /// </summary>
    public bool AcknowledgeCustomSourceRisk { get; init; }
}
