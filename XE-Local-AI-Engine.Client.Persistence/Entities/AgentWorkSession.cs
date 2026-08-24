namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSession
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public byte[] Objective { get; set; } = [];
    public AgentWorkSessionKind Kind { get; set; }
    public Guid AgentDefinitionId { get; set; }
    public Guid ConversationId { get; set; }
    public AgentWorkSessionStatus Status { get; set; }
    public Guid? CurrentTaskId { get; set; }
    public int StepCount { get; set; }
    public Guid? LastCheckpointId { get; set; }

    /// <summary>
    ///     The session's single monotonic change watermark. Every child insert and every task/artifact mutation takes a
    ///     fresh value from here inside the transaction that owns the session row, so one number answers "what changed
    ///     since?" across all five child tables.
    /// </summary>
    public long LastSequence { get; set; }

    public int ConfigVersion { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long Version { get; set; }
}
