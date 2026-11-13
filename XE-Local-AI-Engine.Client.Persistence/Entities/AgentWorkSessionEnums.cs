namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public enum AgentWorkSessionKind
{
    General,
    Research,

    /// <summary>
    ///     Reserved, and now permanently so: this member was held for the Dev Mode to chat series, which was superseded.
    ///     The store keeps rejecting it at creation.
    /// </summary>
    /// <remarks>Nothing will claim it, but removing it would touch merged, migrated rows for no gain.</remarks>
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
