namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionArtifact
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public AgentWorkSessionArtifactKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsValid { get; set; }
    public string ManagedReference { get; set; } = string.Empty;
    public int CreatedStep { get; set; }
}
