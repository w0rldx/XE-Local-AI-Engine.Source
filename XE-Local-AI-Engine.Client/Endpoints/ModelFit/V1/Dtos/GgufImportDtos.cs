namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

public sealed class GgufImportCapabilityResponse
{
    public required bool Available { get; init; }
}

public sealed class PreviewGgufImportRequest
{
    public required string SourcePath { get; init; }
}

public sealed class PreviewGgufImportResponse
{
    public required string ModelBaseName { get; init; }
    public string? DetectedQuantization { get; init; }
    public required IReadOnlyList<string> CanonicalQuantizationChoices { get; init; }
    public string? CanonicalModelName { get; init; }
    public string? FinalFileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string SourceDisplayName { get; init; }
    public string? Architecture { get; init; }
    public uint? GgufVersion { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public bool? HasSufficientStorage { get; init; }
    public required string PreviewToken { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed class StartGgufImportRequest
{
    public required string SourcePath { get; init; }
    public required string PreviewToken { get; init; }
    public required string ModelBaseName { get; init; }
    public required string Quantization { get; init; }
}

public sealed class GgufAcquisitionTicketResponse
{
    public required Guid OperationId { get; init; }
    public required string OperationKind { get; init; }
    public required string ModelName { get; init; }
}

public sealed class GgufAcquisitionStatusResponse
{
    public required Guid OperationId { get; init; }
    public required string OperationKind { get; init; }
    public required string ModelName { get; init; }
    public required string Phase { get; init; }
    public long? CompletedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public string? ErrorCode { get; init; }
    public string? SanitizedMessage { get; init; }
}

public sealed class ListGgufImportsResponse
{
    public required IReadOnlyList<GgufAcquisitionStatusResponse> Items { get; init; }
}

public sealed class GgufImportOperationRequest
{
    public Guid OperationId { get; init; }
}

public sealed class CancelGgufImportResponse
{
    public required Guid OperationId { get; init; }
    public required bool CancellationRequested { get; init; }
    public required GgufAcquisitionStatusResponse Status { get; init; }
}

public sealed class GgufImportErrorResponse
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
}
