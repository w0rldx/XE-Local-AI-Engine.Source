namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

/// <summary>
///     Discriminator for a normalized <see cref="OrchestrationUpdate" /> emitted by
///     <see cref="IOrchestrationRunSession.WatchAsync" />. Workflow-type-agnostic so <c>.Client.Application</c> can
///     map the stream onto the existing single-agent transport without referencing
///     <c>Microsoft.Agents.AI.Workflows</c>.
/// </summary>
public enum OrchestrationUpdateKind
{
    /// <summary>An assistant text delta. <see cref="OrchestrationUpdate.Text" /> carries the fragment.</summary>
    TextDelta = 0,

    /// <summary>A reasoning/thinking text delta. <see cref="OrchestrationUpdate.Text" /> carries the fragment.</summary>
    ReasoningDelta = 1,

    /// <summary>
    ///     A tool-approval request the run is paused on. <see cref="OrchestrationUpdate.RequestId" /> is the durable
    ///     correlation key passed back to <see cref="IOrchestrationRunSession.RespondToApprovalAsync" />;
    ///     <see cref="OrchestrationUpdate.ToolName" /> is the tool awaiting approval.
    /// </summary>
    ApprovalRequest = 2,

    /// <summary>
    ///     The terminal workflow output (the run has produced its final result and gone idle). No further deltas
    ///     follow for this turn.
    /// </summary>
    TerminalOutput = 3,

    /// <summary>The run failed. <see cref="OrchestrationUpdate.Text" /> carries the failure message.</summary>
    Failure = 4
}

/// <summary>
///     One normalized item from a handoff orchestration run. A flat record over a <see cref="Kind" /> discriminator
///     keeps it trivially switchable by the runner; only the fields relevant to the kind are populated. Every update
///     optionally carries the emitting participant (<see cref="ParticipantKey" /> / <see cref="ParticipantName" />)
///     so the chat UI can attribute deltas to an agent.
/// </summary>
public sealed record OrchestrationUpdate
{
    public required OrchestrationUpdateKind Kind { get; init; }

    /// <summary>
    ///     The participant key (the participant <c>AgentDefinition.Id</c>) the update originates from, when known.
    ///     Null for run-level events (e.g. terminal output) that do not belong to a single participant.
    /// </summary>
    public string? ParticipantKey { get; init; }

    /// <summary>The emitting participant's display name, when known. A convenience companion to <see cref="ParticipantKey" />.</summary>
    public string? ParticipantName { get; init; }

    /// <summary>
    ///     Text payload for <see cref="OrchestrationUpdateKind.TextDelta" />,
    ///     <see cref="OrchestrationUpdateKind.ReasoningDelta" />, and
    ///     <see cref="OrchestrationUpdateKind.Failure" /> (the failure message). Null otherwise.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>The approval correlation key for <see cref="OrchestrationUpdateKind.ApprovalRequest" />. Null otherwise.</summary>
    public string? RequestId { get; init; }

    /// <summary>The tool awaiting approval for <see cref="OrchestrationUpdateKind.ApprovalRequest" />. Null otherwise.</summary>
    public string? ToolName { get; init; }

    public static OrchestrationUpdate TextFragment(string text, string? participantKey, string? participantName) => new()
    {
        Kind = OrchestrationUpdateKind.TextDelta,
        Text = text,
        ParticipantKey = participantKey,
        ParticipantName = participantName
    };

    public static OrchestrationUpdate ReasoningFragment(string text, string? participantKey, string? participantName) => new()
    {
        Kind = OrchestrationUpdateKind.ReasoningDelta,
        Text = text,
        ParticipantKey = participantKey,
        ParticipantName = participantName
    };

    public static OrchestrationUpdate Approval(string requestId, string toolName, string? participantKey, string? participantName) => new()
    {
        Kind = OrchestrationUpdateKind.ApprovalRequest,
        RequestId = requestId,
        ToolName = toolName,
        ParticipantKey = participantKey,
        ParticipantName = participantName
    };

    public static OrchestrationUpdate Terminal() => new()
    {
        Kind = OrchestrationUpdateKind.TerminalOutput
    };

    public static OrchestrationUpdate Failed(string message, string? participantKey, string? participantName) => new()
    {
        Kind = OrchestrationUpdateKind.Failure,
        Text = message,
        ParticipantKey = participantKey,
        ParticipantName = participantName
    };
}
