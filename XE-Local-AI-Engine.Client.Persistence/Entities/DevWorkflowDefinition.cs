namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The nodes and edges. One opaque encrypted blob, never queried into — per-node instructions live in it.</summary>
    public byte[] GraphJson { get; set; } = [];

    /// <summary>SHA-256 of the graph bytes, so a run can be grouped by "which graph did this actually run".</summary>
    public string GraphHash { get; set; } = string.Empty;

    /// <summary>
    ///     Denormalized node count, written alongside <see cref="GraphHash" /> at every save. The definition list
    ///     promises never to load the graph blob, and counting at save time — where the graph is already in hand for
    ///     hashing — is what keeps that promise true instead of decrypting every definition on every list call.
    /// </summary>
    public int NodeCount { get; set; }

    public DevWorkflowDefinitionSource Source { get; set; }
    public string? SeedSlug { get; set; }

    /// <summary>
    ///     Delete is an archive flag: the definition disappears from the picker while in-flight and historical runs,
    ///     which carry their own pinned copy of the graph, are unaffected.
    /// </summary>
    public bool Archived { get; set; }

    public int Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
