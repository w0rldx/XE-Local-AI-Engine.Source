namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persisted classification for a single local model, keyed by model name. Not encrypted — a model name, content
///     digest, detected capabilities and kind are not secrets.
/// </summary>
/// <remarks>
///     The override is operator-set and survives a re-download (keyed by name). Nothing writes the detected fields any
///     more; they are only read back from rows an older node stored.
/// </remarks>
internal sealed record class ModelClassification
{
    /// <summary>Model name (primary key, <c>NOCASE</c> collation). The stable override key — survives re-pull.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Content hash stored by an older node; null on any row written now.</summary>
    public string? Digest { get; set; }

    /// <summary>Classification stored by an older node; <see cref="ModelKind.Unknown" /> on any row written now.</summary>
    public ModelKind DetectedKind { get; set; } = ModelKind.Unknown;

    /// <summary>Raw capabilities array as JSON stored by an older node; null on any row written now.</summary>
    public string? DetectedCapabilitiesJson { get; set; }

    /// <summary>Operator override; <c>null</c> means follow <see cref="DetectedKind" />.</summary>
    public ModelKind? OverrideKind { get; set; }

    /// <summary>Unix ms of a detection by an older node; null on any row written now.</summary>
    public long? DetectedAtUtc { get; set; }

    /// <summary>Unix ms of the last row write.</summary>
    public long UpdatedAtUtc { get; set; }
}
