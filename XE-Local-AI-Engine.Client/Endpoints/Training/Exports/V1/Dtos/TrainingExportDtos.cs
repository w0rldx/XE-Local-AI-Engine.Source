namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Route-bound and body-bound fields live in one request object, so none of them can be <c>required</c>: the body
///     is deserialized before the route value is applied, and a required route property fails that deserialization
///     with a confusing 400. The validator carries the real requirements instead.
/// </summary>
public sealed class StartTrainingExportRequest
{
    public Guid RunId { get; init; }

    /// <summary>
    ///     <c>MergedGguf</c> merges the adapter into the base and quantizes the result; <c>AdapterGguf</c> converts
    ///     the adapter alone, to be served with the installed base model underneath it.
    /// </summary>
    public TrainingArtifactKind? Kind { get; init; }

    /// <summary>Target quantization for a merged export. Ignored for an adapter, which is always F16.</summary>
    public string? QuantType { get; init; }
}

public sealed class TrainingRunArtifactsRequest
{
    public Guid RunId { get; init; }
}

public sealed class TrainingArtifactByIdRequest
{
    public Guid ArtifactId { get; init; }
}

/// <summary>
///     The version guard travels in the body (the generated client's shape for a DELETE, matching the dataset delete),
///     so the route-bound id must not be <c>required</c> — see <see cref="StartTrainingExportRequest" />.
/// </summary>
public sealed class DeleteTrainingArtifactRequest
{
    public Guid ArtifactId { get; init; }

    public long ExpectedVersion { get; init; }
}

/// <summary>Route id plus a body field; see <see cref="StartTrainingExportRequest" /> for why neither is required.</summary>
public sealed class PromoteTrainingArtifactRequest
{
    public Guid ArtifactId { get; init; }

    /// <summary>The base name to register under. The quantization suffix is appended by the registry's own naming.</summary>
    public string ModelName { get; init; } = string.Empty;
}

public sealed class TrainingArtifactResponse
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required string Kind { get; init; }

    /// <summary>The staged file's name only. The absolute path stays server-side.</summary>
    public required string FileName { get; init; }

    public string? Sha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required string SmokeState { get; init; }

    public string? SmokeReason { get; init; }

    /// <summary>The registry name once promoted; null while the artifact is still staged and inert.</summary>
    public string? CommittedModelName { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class ListTrainingArtifactsResponse
{
    public required IReadOnlyList<TrainingArtifactResponse> Items { get; init; }
}

public sealed class TrainingExportAcceptedResponse
{
    public required Guid RunId { get; init; }

    public required string Kind { get; init; }

    public required string QuantType { get; init; }
}

public sealed class TrainingExportBlockedResponse
{
    public required string Reason { get; init; }

    public required string Message { get; init; }
}

public sealed class TrainingArtifactSmokeResponse
{
    public required string SmokeState { get; init; }

    public string? SmokeReason { get; init; }
}

public sealed class PromoteTrainingArtifactResponse
{
    public required string ModelName { get; init; }
}
