namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Worker-side projection of a persisted <c>ModelClassification</c> row, carrying the raw stored fields.
/// </summary>
/// <remarks>
///     The application-layer classification service computes the effective kind
///     (<c>override ?? detected ?? Unknown</c>); it is intentionally not precomputed here.
/// </remarks>
public sealed record ModelClassificationRecord
{
    public required string ModelName { get; init; }

    public required string? Digest { get; init; }

    public required ModelKind DetectedKind { get; init; }

    public required string? DetectedCapabilitiesJson { get; init; }

    public required ModelKind? OverrideKind { get; init; }

    public required long? DetectedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}
