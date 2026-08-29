namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public enum AgentWorkSessionKind
{
    General,
    Research,

    /// <summary>
    ///     Reserved, and now permanently so: this member was held for the Dev Mode to chat series, which the Development
    ///     Workflows module supersedes. Nothing will claim it, but removing it would touch merged, migrated rows for no
    ///     gain, so the store keeps rejecting it at creation. New workflow-driven sessions use <see cref="Workflow" />.
    /// </summary>
    Development,

    /// <summary>
    ///     A session owned by one agent node-run of a development workflow. The session is execution scratch — the
    ///     transcript, the checkpoints and the stepwise state that make pause/restart/resume work — while the
    ///     <c>dev_workflow_*</c> tables are the audit.
    /// </summary>
    Workflow
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
