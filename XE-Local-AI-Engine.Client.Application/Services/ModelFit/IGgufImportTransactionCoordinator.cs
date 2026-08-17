namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using AcquisitionKind = GgufAcquisitionOperationKind;
using PreflightKind = XE_Local_AI_Engine.Client.Services.Models.GgufAcquisitionOperationKind;

public sealed record PreviewGgufImportResult(
    string ModelBaseName,
    string? DetectedQuantization,
    IReadOnlyList<string> CanonicalQuantizationChoices,
    string? CanonicalModelName,
    string? FinalFileName,
    long SizeBytes,
    string SourceDisplayName,
    string? Architecture,
    uint? GgufVersion,
    IReadOnlyList<string> Warnings,
    bool? HasSufficientStorage,
    string PreviewToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record StartGgufImportCommand(
    string SourcePath,
    string PreviewToken,
    string ModelBaseName,
    string Quantization);

public sealed record GgufImportTicket(Guid OperationId, string OperationKind, string ModelName);

public sealed class GgufImportApplicationException(string errorCode, string sanitizedMessage) : Exception(sanitizedMessage)
{
    public string ErrorCode { get; } = errorCode;
}

public interface IGgufImportTransactionCoordinator
{
    Task<PreviewGgufImportResult> PreviewAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<GgufImportTicket> StartAsync(StartGgufImportCommand command, CancellationToken cancellationToken = default);
    bool Cancel(Guid operationId);
    GgufAcquisitionStatus? GetStatus(Guid operationId);
    IReadOnlyList<GgufAcquisitionStatus> ListStatuses();
}
