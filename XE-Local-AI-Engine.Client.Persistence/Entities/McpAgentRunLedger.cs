namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class McpAgentRunLedger
{
    public int Id { get; set; }
    public int AccountingVersion { get; set; }
    public long NonterminalRunCount { get; set; }
    public long QueuedRunCount { get; set; }
    public long RunningRunCount { get; set; }
    public long IdentityCount { get; set; }
    public long ActivePayloadBytes { get; set; }
    public long TombstoneLogicalBytes { get; set; }
    public long UpdatedAtUtc { get; set; }
}
