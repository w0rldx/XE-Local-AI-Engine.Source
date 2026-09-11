namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     Where an image pull has got to, aggregated from the daemon's per-layer stream.
///     <para>
///         An application image is large and the pull is the slowest thing a first install does, so this is the only
///         evidence a user has that the install is working rather than hung. The byte counters are best-effort: the
///         daemon reports a total for some layers and not others, so <see cref="TotalBytes" /> of <c>0</c> means "the
///         daemon did not say" and a caller must render the layer counts instead of a false percentage.
///     </para>
/// </summary>
public sealed record ContainerPullProgress
{
    /// <summary>The digest-pinned image being pulled.</summary>
    public required string ImageReference { get; init; }

    /// <summary>How many layers the daemon has mentioned so far.</summary>
    public required int LayerCount { get; init; }

    /// <summary>How many of those layers are complete.</summary>
    public required int CompletedLayers { get; init; }

    /// <summary>Bytes transferred so far across every layer.</summary>
    public required long CurrentBytes { get; init; }

    /// <summary>Total bytes across every layer, or <c>0</c> when the daemon did not report one.</summary>
    public required long TotalBytes { get; init; }
}
