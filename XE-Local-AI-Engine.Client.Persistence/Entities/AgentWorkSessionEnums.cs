namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public enum AgentWorkSessionKind
{
    General,
    Research,

    /// <summary>
    ///     Reserved. Development sessions join only after the Dev Mode to chat series lands, so the store rejects this
    ///     kind at creation rather than persisting rows no code path can execute.
    /// </summary>
    Development
}

public enum AgentWorkSessionStatus
{
    Draft,
    Running,
    Paused,
    WaitingForInput,
    WaitingForApproval,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public enum AgentWorkSessionTaskStatus
{
    Planned,
    Active,
    Blocked,
    Done,
    Dropped
}

public enum AgentWorkSessionTaskOrigin
{
    User,
    Agent
}

public enum AgentWorkSessionFindingKind
{
    Finding,
    Evidence,
    Decision,
    OpenQuestion
}

public enum AgentWorkSessionArtifactKind
{
    Report,
    Note,
    File,
    Patch
}
