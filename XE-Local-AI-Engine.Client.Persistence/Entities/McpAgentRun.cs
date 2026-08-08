namespace XE_Local_AI_Engine.Client.Persistence.Entities;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record class McpAgentRun
{
    public Guid RequestId { get; set; }
    public byte[] RequestFingerprint { get; set; } = [];
    public int AccountingVersion { get; set; }
    public McpAgentRunStatus Status { get; set; }
    public long Version { get; set; }
    public Guid? ClaimToken { get; set; }
    public McpAgentRunStopReason StopReason { get; set; }
    public long? StopRequestedAtUtc { get; set; }
    public Guid? AgentDefinitionId { get; set; }
    public long? AgentDefinitionVersion { get; set; }
    public string? ModelId { get; set; }
    public string? ModelOverrideId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public byte[]? BindingFingerprint { get; set; }
    public byte[]? TaskPayload { get; set; }
    public byte[]? InstructionsPayload { get; set; }
    public byte[]? ResultPayload { get; set; }
    public byte[]? DisplayPayload { get; set; }
    public string? FailureCode { get; set; }
    public long ReservedActivePayloadBytes { get; set; }
    public long ActivePayloadBytes { get; set; }
    public long TombstoneLogicalBytes { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? ClaimedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public long? PayloadExpiresAtUtc { get; set; }
    public long? CompactedAtUtc { get; set; }
}
