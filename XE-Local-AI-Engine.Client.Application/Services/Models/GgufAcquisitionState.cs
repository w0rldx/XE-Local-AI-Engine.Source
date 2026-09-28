namespace XE_Local_AI_Engine.Client.Services.Models;

public sealed class GgufAcquisitionState
{
    public required GgufAcquisitionDisposition Disposition { get; init; }

    public required ProviderMapDisposition ProviderMapDisposition { get; init; }

    public string? ConflictingProvider { get; init; }

    public Guid? ActiveOperationId { get; init; }
}
