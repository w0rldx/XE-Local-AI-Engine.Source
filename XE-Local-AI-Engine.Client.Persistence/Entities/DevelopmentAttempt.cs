namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentAttempt
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid? PredecessorAttemptId { get; set; }
    public DevelopmentAttemptRole Role { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public DevelopmentAttemptStatus Status { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? EndedAtUtc { get; set; }
    public string? TerminalReason { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public Guid StartOperationId { get; set; }

    /// <summary>
    ///     The canonical command profile this attempt actually ran under, captured at attempt creation and immutable
    ///     thereafter. Stored as plaintext by design — see <c>DevelopmentAttemptConfiguration</c> for the rationale.
    ///     <para>
    ///         Null on attempts created before this column existed. A null means "no attempt-level snapshot", and the
    ///         readers fall back to the project's current profile, which is exactly the pre-existing behaviour.
    ///     </para>
    /// </summary>
    public string? CommandProfileJson { get; set; }

    public long Version { get; set; }
}
