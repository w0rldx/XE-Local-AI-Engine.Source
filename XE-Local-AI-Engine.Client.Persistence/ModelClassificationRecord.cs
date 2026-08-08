namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Worker-side projection of a persisted <c>ModelClassification</c> row. Carries the raw stored fields; the
///     application-layer classification service computes the effective kind (<c>override ?? detected ?? Unknown</c>)
///     — it is intentionally not precomputed here.
/// </summary>
public sealed record ModelClassificationRecord(
    string ModelName,
    string? Digest,
    ModelKind DetectedKind,
    string? DetectedCapabilitiesJson,
    ModelKind? OverrideKind,
    long? DetectedAtUtc,
    long UpdatedAtUtc);
