namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persisted classification for a single local model, keyed by model name. Not encrypted — a model name,
///     content digest, detected capabilities and kind are not secrets. The detected fields are a digest-keyed cache
///     refreshed on re-pull; the override is operator-set and intentionally survives a re-pull (keyed by name).
/// </summary>
internal sealed record class ModelClassification
{
    /// <summary>Model name (primary key, <c>NOCASE</c> collation). The stable override key — survives re-pull.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Content hash from <c>/api/tags</c>; the cache-validity key for the detected fields. Null until first seen.</summary>
    public string? Digest { get; set; }

    /// <summary>Machine classification from detected capabilities; <see cref="ModelKind.Unknown" /> until probed.</summary>
    public ModelKind DetectedKind { get; set; } = ModelKind.Unknown;

    /// <summary>Raw capabilities array as JSON (e.g. <c>["completion","tools"]</c>) for read-only badges. Null until probed.</summary>
    public string? DetectedCapabilitiesJson { get; set; }

    /// <summary>Operator override; <c>null</c> means follow <see cref="DetectedKind" />.</summary>
    public ModelKind? OverrideKind { get; set; }

    /// <summary>Unix ms of the last successful detection probe; null until probed.</summary>
    public long? DetectedAtUtc { get; set; }

    /// <summary>Unix ms of the last row write (detect or override).</summary>
    public long UpdatedAtUtc { get; set; }
}
