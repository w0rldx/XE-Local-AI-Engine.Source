namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionFinding
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? TaskId { get; set; }
    public long Sequence { get; set; }
    public AgentWorkSessionFindingKind Kind { get; set; }
    public byte[] Text { get; set; } = [];
    public byte[]? SourceRef { get; set; }
    public int CreatedStep { get; set; }
    public bool Superseded { get; set; }
}
