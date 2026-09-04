namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One integration session: the owner of a <see cref="NodeConversation" /> whose <c>kind</c> is
///     <c>integration</c>. Shaped on <see cref="AgentWorkSession" /> minus tasks, findings and checkpoints. Every
///     column is plaintext structural — the transcript lives in the owned conversation, which carries its own
///     encryption.
/// </summary>
internal sealed record class IntegrationSession
{
    public Guid Id { get; set; }

    /// <summary>The trigger that created the session. Loose reference with no FK. Plaintext (structural).</summary>
    public Guid TriggerId { get; set; }

    /// <summary>The integrator identity that owns this session (ruling R4-6). Plaintext (structural).</summary>
    public Guid PrincipalId { get; set; }

    /// <summary>
    ///     The owned conversation's id, pre-minted by the caller <b>before</b> the accept transaction (ruling R4-1).
    ///     For the width of the gap between that commit and the conversation insert this points at a row that does not
    ///     exist yet, on purpose, which is one reason it carries no foreign key. Plaintext (structural).
    /// </summary>
    public Guid ConversationId { get; set; }

    /// <summary>The saved agent the session's executions run. Loose reference with no FK. Plaintext (structural).</summary>
    public Guid AgentDefinitionId { get; set; }

    /// <summary>Whether the session still accepts executions. Plaintext (structural).</summary>
    public IntegrationSessionStatus Status { get; set; }

    /// <summary>Unix-ms creation instant. Plaintext (structural).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>Unix-ms instant of the newest accepted execution or persisted event. Plaintext (structural).</summary>
    public long LastActivityUtc { get; set; }

    /// <summary>How many executions have joined this session. Written only by the accept transaction. Plaintext (structural).</summary>
    public int ExecutionCount { get; set; }

    /// <summary>
    ///     The newest persisted event's sequence, as an activity indicator the UI renders — never an ordering key.
    ///     Sequences restart at 1 per execution, so this is a plain assignment and not a running maximum. Written by
    ///     <c>AppendEventAsync</c> and by <c>TryTerminalizeAsync</c>, and by nothing else. Plaintext (structural).
    /// </summary>
    public long LastSequence { get; set; }
}
