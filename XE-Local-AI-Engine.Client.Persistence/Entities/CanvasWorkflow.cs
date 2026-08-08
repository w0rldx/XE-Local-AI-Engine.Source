namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class CanvasWorkflow
{
    public Guid Id { get; set; }

    /// <summary>Display label. Plaintext for list/search; not part of the encrypted surface.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     The full serialized workflow graph (nodes including agent instructions and Start text, plus edges) as UTF-8
    ///     bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>graph_json</c>. Required: one
    ///     opaque blob — never queried into, because instructions are sensitive.
    /// </summary>
    public byte[] GraphJson { get; set; } = [];

    /// <summary>
    ///     Bumped on each graph change; used for optimistic concurrency on update (a stale expected version is rejected
    ///     as a conflict). Mirrors <see cref="AgentDefinition.Version" />.
    /// </summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
