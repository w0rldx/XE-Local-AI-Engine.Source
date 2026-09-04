namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A named, externally addressable entry point that invokes one saved agent (ADR 0008). Modelled on
///     <see cref="ScheduledJobDefinition" />; every column is plaintext structural, because the name is the external
///     contract and the display fields are sorted and filtered on, exactly as <c>AgentWorkSession.Title</c> is.
/// </summary>
internal sealed record class IntegrationTrigger
{
    public Guid Id { get; set; }

    /// <summary>External-facing name a caller addresses (<c>^[a-z0-9][a-z0-9-]{1,63}$</c>), unique per node. Plaintext (structural).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Operator label shown in the UI. Plaintext (structural).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional operator notes. Plaintext (structural).</summary>
    public string? Description { get; set; }

    /// <summary>Whether the trigger answers invocations at all; a disabled trigger is a 404. Plaintext (structural).</summary>
    public bool Enabled { get; set; }

    /// <summary>What the trigger invokes. V1 has one member. Plaintext (structural).</summary>
    public IntegrationTargetKind TargetKind { get; set; }

    /// <summary>The saved agent definition this trigger runs. Loose reference with no FK. Plaintext (structural).</summary>
    public Guid TargetAgentDefinitionId { get; set; }

    /// <summary>Whether each invocation gets its own session or the caller manages one. Plaintext (structural).</summary>
    public IntegrationSessionPolicy SessionPolicy { get; set; }

    /// <summary>
    ///     Which input kinds the invoke body may carry. A <c>[Flags]</c> combination stored as a plain <c>int</c>
    ///     column — see <see cref="IntegrationInputKinds" /> for why it is not a string conversion. Plaintext (structural).
    /// </summary>
    public IntegrationInputKinds AcceptedInputKinds { get; set; }

    /// <summary>Unix-ms creation instant. Plaintext (structural).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>Unix-ms instant of the last update. Plaintext (structural).</summary>
    public long UpdatedAtUtc { get; set; }

    /// <summary>Optimistic concurrency token; an admin update that loses the compare-and-swap is answered 409. Plaintext (structural).</summary>
    public long Version { get; set; }
}
