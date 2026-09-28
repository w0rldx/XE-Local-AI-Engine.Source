namespace XE_Local_AI_Engine.Client.Services.Models;

public sealed record GgufAcquisitionIntent
{
    public required GgufAcquisitionOperationKind OperationKind { get; init; }

    public required string ModelBaseName { get; init; }

    public required string Quantization { get; init; }

    public GgufProjectorAcquisitionMetadata? Projector { get; init; }

    public GgufDownloadAcquisitionMetadata? Download { get; init; }
}
