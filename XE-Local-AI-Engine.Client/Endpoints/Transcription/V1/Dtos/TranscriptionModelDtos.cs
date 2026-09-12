namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using System.Text.Json.Serialization;

/// <summary>Phase of a coordinated weight download.</summary>
public enum TranscriptionModelDownloadPhaseDto
{
    [JsonStringEnumMemberName("running")]
    Running = 0,

    [JsonStringEnumMemberName("completed")]
    Completed = 1,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled = 2,

    [JsonStringEnumMemberName("failed")]
    Failed = 3
}

/// <summary>A sanitized download status: phase, byte counts, part counters and an operator-safe reason.</summary>
public sealed class TranscriptionModelDownloadStatusResponse
{
    public required TranscriptionModelDownloadPhaseDto Phase { get; init; }

    public long? CompletedBytes { get; init; }

    public long? TotalBytes { get; init; }

    /// <summary>1-based index of the file currently transferring; a model download is the VAD file plus the weights.</summary>
    public int? PartIndex { get; init; }

    public int? PartCount { get; init; }

    /// <summary>An operator-safe failure reason; never a path, a URL or a token.</summary>
    public string? SanitizedError { get; init; }
}

/// <summary>One catalogue row as this node sees it.</summary>
public sealed class TranscriptionModelResponse
{
    public required string Id { get; init; }

    public required string Tier { get; init; }

    public required long SizeBytes { get; init; }

    public required long ApproximateVramBytes { get; init; }

    public required long ApproximateRamBytes { get; init; }

    /// <summary>Whether the weights are English-only. Every V1 row is multilingual.</summary>
    public required bool EnglishOnly { get; init; }

    /// <summary>Whether the weight file is present on disk.</summary>
    public required bool Installed { get; init; }

    /// <summary>The latest download status, when one is tracked for this row.</summary>
    public TranscriptionModelDownloadStatusResponse? Download { get; init; }
}

/// <summary>The catalogue plus the selected and recommended ids.</summary>
public sealed class TranscriptionModelListResponse
{
    public required IReadOnlyList<TranscriptionModelResponse> Models { get; init; }

    /// <summary>The operator's explicit choice; <see langword="null" /> means the recommendation is used.</summary>
    public string? SelectedModelId { get; init; }

    public required string RecommendedModelId { get; init; }
}

/// <summary>Starts or cancels a weight download.</summary>
public sealed class TranscriptionModelDownloadRequest
{
    public required string ModelId { get; init; }
}

/// <summary>The accepted-download identity.</summary>
public sealed class TranscriptionModelDownloadResponse
{
    public required string ModelId { get; init; }

    /// <summary>Whether the request was accepted at all — false only when nothing was in flight to cancel.</summary>
    public required bool Accepted { get; init; }

    /// <summary>Whether an existing download was rejoined instead of a new one started.</summary>
    public bool? AlreadyInFlight { get; init; }

    public TranscriptionModelDownloadStatusResponse? Status { get; init; }
}

/// <summary>Sets or clears the operator's model choice.</summary>
public sealed class SelectTranscriptionModelRequest
{
    /// <summary>
    ///     The catalogue id to select. <see langword="null" /> or blank CLEARS the override, so the node falls back
    ///     to the hardware recommendation — that is a valid request, not a missing field.
    /// </summary>
    public string? ModelId { get; init; }
}
