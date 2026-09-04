namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A saved Graph Workflow: the authored nodes and edges plus the denormalized facts a list page needs about them.
///     A run pins its own copy of the graph, so editing or deleting a definition never rewrites history.
/// </summary>
internal sealed class GraphWorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>A human label for the picker. Plaintext, like the name — it is what the list page shows and sorts near.</summary>
    public string? Description { get; set; }

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

    /// <summary>The graph document's schema version, denormalized for the same reason as the node count.</summary>
    public int SchemaVersion { get; set; }

    public int Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
