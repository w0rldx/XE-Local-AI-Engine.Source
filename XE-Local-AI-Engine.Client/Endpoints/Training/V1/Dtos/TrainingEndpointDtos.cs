namespace XE_Local_AI_Engine.Client.Endpoints.Training.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Wire DTOs for the training dataset module. The persisted <c>…V1</c> payload records
///     (<see cref="DatasetDefinitionBodyV1" />, <see cref="TrainingSampleContentV1" />, …) are reused verbatim as wire
///     types: they are already versioned serialization contracts, so a parallel copy would only be a second place to
///     forget a field.
/// </summary>
public enum TrainingErrorCode
{
    NotFound,
    InvalidRequest,
    VersionConflict,
    GenerationActive,
    DefinitionReferenced,
    TrainingBusy,
    InvalidLifecycleTransition
}

public sealed record TrainingErrorResponse
{
    public required TrainingErrorCode Code { get; init; }

    public required string Message { get; init; }
}

public sealed record TrainingDefinitionResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required TrainingDatasetKind Kind { get; init; }

    public required DatasetDefinitionBodyV1 Body { get; init; }

    /// <summary>The artifact version a generated dataset pins. Bumped on every edit.</summary>
    public required long DefinitionVersion { get; init; }

    /// <summary>The optimistic-concurrency token to send back on the next mutation.</summary>
    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed record ListTrainingDefinitionsResponse
{
    public required IReadOnlyList<TrainingDefinitionResponse> Items { get; init; }
}

public sealed record GetTrainingDefinitionRequest
{
    public Guid DefinitionId { get; init; }
}

public sealed record CreateTrainingDefinitionRequest
{
    public string Name { get; init; } = string.Empty;

    public DatasetDefinitionBodyV1 Body { get; init; } = new();
}

public sealed record UpdateTrainingDefinitionRequest
{
    public Guid DefinitionId { get; init; }

    public long ExpectedVersion { get; init; }

    public string Name { get; init; } = string.Empty;

    public DatasetDefinitionBodyV1 Body { get; init; } = new();
}

public sealed record DeleteTrainingDefinitionRequest
{
    public Guid DefinitionId { get; init; }

    public long ExpectedVersion { get; init; }
}

/// <summary>Starts generation. Carries a body so the POST is never bodyless (the FastEndpoints 415 trap).</summary>
public sealed record GenerateTrainingDatasetRequest
{
    public Guid DefinitionId { get; init; }

    public long ExpectedVersion { get; init; }

    public string Name { get; init; } = string.Empty;
}

public sealed record TrainingDatasetResponse
{
    public required Guid Id { get; init; }

    public required Guid DefinitionId { get; init; }

    public required long DefinitionVersion { get; init; }

    public required string Name { get; init; }

    public required TrainingDatasetStatus Status { get; init; }

    /// <summary>Bumped by any sample mutation; the content fingerprint is recomputed with it.</summary>
    public required int Revision { get; init; }

    public string? ContentFingerprint { get; init; }

    public required int TotalSampleCount { get; init; }

    public required int GoodSampleCount { get; init; }

    public required int BadSampleCount { get; init; }

    public required int RejectedSampleCount { get; init; }

    public required int DuplicateSampleCount { get; init; }

    public DatasetGenerationWorkStatus? WorkStatus { get; init; }

    public string? WorkErrorMessage { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed record ListTrainingDatasetsResponse
{
    public required IReadOnlyList<TrainingDatasetResponse> Items { get; init; }
}

public sealed record GetTrainingDatasetRequest
{
    public Guid DatasetId { get; init; }
}

public sealed record DeleteTrainingDatasetRequest
{
    public Guid DatasetId { get; init; }

    public long ExpectedVersion { get; init; }
}

public sealed record ListTrainingSamplesRequest
{
    public Guid DatasetId { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;

    public TrainingSampleLabel? Label { get; init; }

    public TrainingSampleReviewState? ReviewState { get; init; }

    public string? Kind { get; init; }
}

public sealed record TrainingSampleResponse
{
    public required Guid Id { get; init; }

    public required Guid DatasetId { get; init; }

    public required int Sequence { get; init; }

    public required string Kind { get; init; }

    public required TrainingSampleLabel Label { get; init; }

    public required TrainingSampleReviewState ReviewState { get; init; }

    public required TrainingSampleProvenance Provenance { get; init; }

    public required string SourceHash { get; init; }

    public required TrainingSampleContentV1 Content { get; init; }

    public TrainingSampleValidationV1? Validation { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed record ListTrainingSamplesResponse
{
    public required IReadOnlyList<TrainingSampleResponse> Items { get; init; }

    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public sealed record ReviewTrainingSampleRequest
{
    public Guid DatasetId { get; init; }

    public Guid SampleId { get; init; }

    public TrainingSampleReviewVerb Verb { get; init; }

    /// <summary>Required by <see cref="TrainingSampleReviewVerb.Relabel" />, ignored otherwise.</summary>
    public TrainingSampleLabel? Label { get; init; }
}

public sealed record ExportTrainingDatasetRequest
{
    public Guid DatasetId { get; init; }

    public DatasetExportFormat Format { get; init; } = DatasetExportFormat.Jsonl;
}

public sealed record ExportTrainingDatasetResponse
{
    public required Guid DatasetId { get; init; }

    public required DatasetExportFormat Format { get; init; }

    /// <summary>The whole export as newline-delimited JSON. Rejected samples are excluded.</summary>
    public required string Content { get; init; }

    public required int LineCount { get; init; }
}

public sealed record ToolMockResponse
{
    public required Guid Id { get; init; }

    public required string ToolName { get; init; }

    public required ToolMockBodyV1 Body { get; init; }

    public ToolMockVerificationV1? Verification { get; init; }

    public required ToolMockVerificationState VerificationState { get; init; }

    public required bool Enabled { get; init; }

    public required long Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed record ListToolMocksResponse
{
    public required IReadOnlyList<ToolMockResponse> Items { get; init; }
}

public sealed record GetToolMockRequest
{
    public Guid MockId { get; init; }
}

public sealed record CreateToolMockRequest
{
    public string ToolName { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public ToolMockBodyV1 Body { get; init; } = new();
}

public sealed record UpdateToolMockRequest
{
    public Guid MockId { get; init; }

    public long ExpectedVersion { get; init; }

    public string ToolName { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public ToolMockBodyV1 Body { get; init; } = new();
}

public sealed record DeleteToolMockRequest
{
    public Guid MockId { get; init; }

    public long ExpectedVersion { get; init; }
}

public sealed record VerifyToolMockRequest
{
    public Guid MockId { get; init; }

    public long ExpectedVersion { get; init; }
}
