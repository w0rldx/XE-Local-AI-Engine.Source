namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

/// <summary>
///     Wire shapes for the managed Linux CUDA source-build lane.
/// </summary>
/// <remarks>
///     The backend, source-selection, revision-mode and validity enums are NOT redeclared here: the transcription
///     runtime DTOs already publish them, and a second set under different names would give the generated client two
///     incompatible spellings of the same value.
/// </remarks>
public sealed class StartWhisperCppSourceBuildRequest
{
    public required TranscriptionBackendDto Backend { get; init; }

    public required TranscriptionSourceSelectionDto Source { get; init; }

    /// <summary>Ignored for the official source, which the server pins itself.</summary>
    public string? Repository { get; init; }

    /// <summary>A full 40-character SHA. Rejected for the official source, which builds the engine-pinned revision.</summary>
    public string? Commit { get; init; }

    /// <summary>
    ///     Operator acknowledgement that a custom repository's build scripts execute with the app user's privileges.
    ///     Required for a custom source.
    /// </summary>
    public required bool AcknowledgeCustomSourceRisk { get; init; }
}

/// <summary>Which backend's prerequisite checklist to report.</summary>
public sealed class GetWhisperCppSourceBuildPrerequisitesRequest
{
    public required TranscriptionBackendDto Backend { get; init; }
}

/// <summary>One prerequisite row, so a refusal names the tool that is missing instead of just refusing.</summary>
public sealed class WhisperCppSourceBuildPrerequisiteItemResponse
{
    public required string Key { get; init; }

    public required bool Satisfied { get; init; }

    public required string Detail { get; init; }
}

public sealed class WhisperCppSourceBuildPrerequisitesResponse
{
    public required TranscriptionBackendDto Backend { get; init; }

    public required IReadOnlyList<WhisperCppSourceBuildPrerequisiteItemResponse> Items { get; init; }

    public required bool CanBuild { get; init; }
}

public sealed class WhisperCppSourceBuildDescriptorResponse
{
    public required Guid BuildId { get; init; }

    public required TranscriptionBackendDto Backend { get; init; }

    public required TranscriptionSourceSelectionDto Source { get; init; }

    public required string Repository { get; init; }

    public required TranscriptionSourceRevisionModeDto RevisionMode { get; init; }

    public string? RequestedCommit { get; init; }

    /// <summary>The commit the checkout landed on; null until the build has verified it.</summary>
    public string? ResolvedCommit { get; init; }
}

/// <summary>The poll shape the operator UI renders while a build runs.</summary>
public sealed class WhisperCppSourceBuildStatusResponse
{
    public required string Phase { get; init; }

    public required bool IsRunning { get; init; }

    public required bool Terminal { get; init; }

    /// <summary>
    ///     Sequence number of the first retained line, so a client can tell a dropped prefix from a rewound log.
    /// </summary>
    public required long LogStartSequence { get; init; }

    public required IReadOnlyList<string> LogLines { get; init; }

    /// <summary>Display-safe failure reason; never a path, URL or command line.</summary>
    public string? SanitizedError { get; init; }

    public WhisperCppSourceBuildDescriptorResponse? CurrentBuild { get; init; }

    public long? StartedAtUtc { get; init; }

    public long? CompletedAtUtc { get; init; }
}

public sealed class StartWhisperCppSourceBuildResponse
{
    public required bool Started { get; init; }

    public required WhisperCppSourceBuildStatusResponse Status { get; init; }
}
