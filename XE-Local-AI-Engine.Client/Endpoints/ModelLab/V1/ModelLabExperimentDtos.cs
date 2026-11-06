namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class ModelLabCheckpointDownloadRequest
{
    public required string RepoId { get; init; }

    public string? Revision { get; init; }

    public required string InstalledModelName { get; init; }

    /// <summary>The operator has confirmed the preflight's license text.</summary>
    public required bool LicenseConfirmed { get; init; }
}

/// <summary>A refused pick is a 200 with the preflight's refusal set, and no artifact.</summary>
public sealed class ModelLabCheckpointDownloadResponse
{
    public required ModelLabCheckpointPreflightResponse Preflight { get; init; }

    public required Guid? BaseArtifactId { get; init; }

    public required string? ArtifactStatus { get; init; }
}

public sealed class StartModelLabExperimentRequest
{
    public required Guid BaseArtifactId { get; init; }

    public required string InstalledModelName { get; init; }

    public required string Prompt { get; init; }

    public required bool LicenseConfirmed { get; init; }

    /// <summary>Makes this a steering job. Left out, it is a trace.</summary>
    public ModelLabSteeringRequest? Steering { get; init; }

    /// <summary>The distinct blocks a trace records, in any order. Left out, the three quarter points; not allowed with <see cref="Steering" />.</summary>
    public IReadOnlyList<int>? Layers { get; init; }
}

public sealed class StartModelLabExperimentResponse
{
    public required Guid ExperimentId { get; init; }
}

/// <summary>A start refused before anything was written. <see cref="Reason" /> is a stable code; the message is what to do next.</summary>
public sealed class ModelLabStartBlockedResponse
{
    public required ModelLabStartRefusal Reason { get; init; }

    public required string Message { get; init; }
}

public sealed class ModelLabExperimentResponse
{
    public required Guid Id { get; init; }

    public required ModelLabExperimentStatus Status { get; init; }

    public required ModelLabExperimentKind Kind { get; init; }

    public required string RepoId { get; init; }

    public required string Revision { get; init; }

    public required string InstalledModelName { get; init; }

    public required string Prompt { get; init; }

    public required string? ErrorMessage { get; init; }

    /// <summary>A trace's size; null for a steering job, which has no trace to read.</summary>
    public required long? TraceByteCount { get; init; }

    /// <summary>Set on the single-experiment read: a finished trace, or as much as a steering job has done. A list leaves it out.</summary>
    public required ModelLabTraceResult? Result { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed class ModelLabExperimentListResponse
{
    /// <summary>False on a platform with no trace job. The page shows <see cref="UnsupportedMessage" /> where a trace would start.</summary>
    public required bool PlatformSupported { get; init; }

    public required string? UnsupportedMessage { get; init; }

    public required Guid? ActiveExperimentId { get; init; }

    public required int MaxRetainedExperiments { get; init; }

    public required IReadOnlyList<ModelLabExperimentResponse> Experiments { get; init; }
}

public sealed class ModelLabExperimentRouteRequest
{
    public Guid ExperimentId { get; init; }
}

/// <summary>One token at one layer, as measured.</summary>
public sealed class ModelLabTraceRecordResponse
{
    public required int Step { get; init; }

    public required int Layer { get; init; }

    /// <summary>F for a full-attention block, L for a linear one, ? when the model does not say.</summary>
    public required string Kind { get; init; }

    /// <summary>Null, here and in the lists below, where the measured value was not a finite number.</summary>
    public required double? Norm { get; init; }

    /// <summary>The 1st, 50th and 99th percentiles of the residual's values.</summary>
    public required IReadOnlyList<double?> Percentiles { get; init; }

    public required IReadOnlyList<int> TopIndices { get; init; }

    public required IReadOnlyList<double?> TopValues { get; init; }
}

public sealed class ModelLabTraceResponse
{
    public required IReadOnlyList<ModelLabTraceRecordResponse> Records { get; init; }

    public required bool Truncated { get; init; }

    public required int DroppedRecords { get; init; }

    public required long ByteCap { get; init; }
}
