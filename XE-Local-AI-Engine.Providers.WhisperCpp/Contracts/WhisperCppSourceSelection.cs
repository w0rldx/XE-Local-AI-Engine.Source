namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Which repository a managed source build compiles from.</summary>
public enum WhisperCppSourceSelection
{
    /// <summary>The engine-pinned official whisper.cpp repository at the engine-pinned revision.</summary>
    Official = 0,

    /// <summary>An operator-supplied public GitHub repository, which requires an explicit risk acknowledgement.</summary>
    Custom = 1
}

/// <summary>Which revision of the selected repository a managed source build compiles.</summary>
public enum WhisperCppSourceRevisionMode
{
    /// <summary>The revision this engine pins (<see cref="WhisperCppReleasePins.PinnedSourceCommitSha" />).</summary>
    EnginePinned = 0,

    /// <summary>Whatever the repository's default branch points at when the build starts.</summary>
    DefaultBranch = 1,

    /// <summary>An operator-supplied full 40-character commit SHA.</summary>
    ExplicitCommit = 2
}

/// <summary>
///     An operator's request to compile a managed whisper.cpp runtime from source.
/// </summary>
/// <remarks>
///     This record ships here, with the two enums above, rather than with the rest of the source-build contract
///     surface: <see cref="WhisperCppSourceBuildRequestValidation.Normalize" /> takes it, and that validation has to be
///     present as soon as <c>WhisperInstalledRuntimeState</c> exists, because the installed-runtime store validates a
///     stored repository by round-tripping it through the same normalizer. The service interface, the descriptor, the
///     phase/outcome enums and the status record arrive with the source-build lane itself.
/// </remarks>
public sealed record WhisperCppSourceBuildRequest
{
    /// <summary>The acceleration backend to compile for.</summary>
    public required WhisperBackend Backend { get; init; }

    /// <summary>Official or operator-supplied repository.</summary>
    public required WhisperCppSourceSelection Source { get; init; }

    /// <summary>The repository URL; ignored (and pinned by the server) for the official source.</summary>
    public string? Repository { get; init; }

    /// <summary>The requested commit; rejected for the official source, which uses the engine-pinned revision.</summary>
    public string? Commit { get; init; }

    /// <summary>
    ///     Operator acknowledgement that a custom repository's build scripts execute with the app user's privileges.
    ///     Required for <see cref="WhisperCppSourceSelection.Custom" />.
    /// </summary>
    public bool AcknowledgeCustomSourceRisk { get; init; }
}
