namespace XE_Local_AI_Engine.Client.Services.Models;

public sealed class ResolvedGgufAcquisitionIdentity
{
    public required string CanonicalModelName { get; init; }

    public required string ModelReservationKey { get; init; }

    public required string CanonicalQuantization { get; init; }

    public required string FinalFileName { get; init; }

    public required string RelativeGgufPath { get; init; }

    public required string RelativeSidecarPath { get; init; }

    public required string? ProjectorFileName { get; init; }

    public required string? ProjectorRelativePath { get; init; }
}
