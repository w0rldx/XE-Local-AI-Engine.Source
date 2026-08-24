namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionCheckpoint
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public int Step { get; set; }

    /// <summary>
    ///     Nullable on purpose: every compaction no-op outcome is non-fatal, and a node with no local model must still
    ///     be able to checkpoint its structured state.
    /// </summary>
    public byte[]? Summary { get; set; }

    public byte[] StateJson { get; set; } = [];
    public long CreatedAtUtc { get; set; }
}
