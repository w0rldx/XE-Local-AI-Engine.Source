namespace XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class PreviewGgufImportResult
{
    public required string ModelBaseName { get; init; }

    public required string? DetectedQuantization { get; init; }

    public required IReadOnlyList<string> CanonicalQuantizationChoices { get; init; }

    public required string? CanonicalModelName { get; init; }

    public required string? FinalFileName { get; init; }

    public required long SizeBytes { get; init; }

    public required string SourceDisplayName { get; init; }

    public required string? Architecture { get; init; }

    public required uint? GgufVersion { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required bool? HasSufficientStorage { get; init; }

    public required string PreviewToken { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed class StartGgufImportCommand
{
    public required string SourcePath { get; init; }

    public required string PreviewToken { get; init; }

    public required string ModelBaseName { get; init; }

    public required string Quantization { get; init; }
}

public sealed class GgufImportTicket
{
    public required Guid OperationId { get; init; }

    public required string OperationKind { get; init; }

    public required string ModelName { get; init; }
}

public sealed class GgufImportApplicationException : Exception
{
    public GgufImportApplicationException(string errorCode, string sanitizedMessage)
        : base(sanitizedMessage) =>
        ErrorCode = errorCode;

    public GgufImportApplicationException(string errorCode, string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException) =>
        ErrorCode = errorCode;

    public string ErrorCode { get; }
}

public interface IGgufImportTransactionCoordinator
{
    Task<PreviewGgufImportResult> PreviewAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<GgufImportTicket> StartAsync(StartGgufImportCommand command, CancellationToken cancellationToken = default);
    bool Cancel(Guid operationId);
    GgufAcquisitionStatus? GetStatus(Guid operationId);
    IReadOnlyList<GgufAcquisitionStatus> ListStatuses();
}
