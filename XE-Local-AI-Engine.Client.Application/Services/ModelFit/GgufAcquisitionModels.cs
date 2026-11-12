namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public enum GgufAcquisitionPhase
{
    // Kept for wire/backward compatibility with callers that only understand a generic active phase.
    Running = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
    Validating = 4,
    Downloading = 5,
    Copying = 6,
    Committing = 7
}

public sealed record GgufAcquisitionStatus
{
    public required Guid OperationId { get; init; }

    public required GgufAcquisitionOperationKind OperationKind { get; init; }

    public required string ModelName { get; init; }

    public required GgufAcquisitionPhase Phase { get; init; }

    public required long? CompletedBytes { get; init; }

    public required long? TotalBytes { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required string? ErrorCode { get; init; }

    public required string? SanitizedError { get; init; }

    /// <summary>
    ///     The request a download was started with, so a retry can replay it; <see langword="null" /> for an import and for
    ///     a row recorded after a restart, whose request nothing on disk keeps.
    /// </summary>
    public GgufModelRequest? DownloadRequest { get; init; }
}

public sealed class GgufAcquisitionRegistration
{
    public required GgufAcquisitionStatus Status { get; init; }

    public required bool AlreadyInFlight { get; init; }

    public required CancellationToken CancellationToken { get; init; }
}
