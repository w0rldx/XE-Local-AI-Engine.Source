namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One entry in an instance's lifecycle history. A plain class rather than a record class, matching the sibling
///     event tables it copies (<see cref="IntegrationExecutionEvent" />, <see cref="DevWorkflowRunEvent" />).
/// </summary>
internal sealed class ExternalAppInstanceEvent
{
    /// <summary>
    ///     A <c>Guid</c> rather than an autoincrement <c>long</c>, matching the sibling event tables. The brief named no
    ///     surrogate key; this shape is the deliberate deviation ruled in R1-5.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>The owning instance. Plaintext (structural).</summary>
    public Guid InstanceId { get; set; }

    /// <summary>
    ///     Monotonic per instance, starting at 1 with the install's <c>PermissionAccepted</c> event. Minted by the store
    ///     inside the compare-and-swap transaction that writes the status, so there are no holes: a lost CAS writes
    ///     nothing at all, not even the event.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>What happened. Plaintext (structural).</summary>
    public ExternalAppInstanceEventKind Kind { get; set; }

    /// <summary>
    ///     A small JSON elaboration, or null. <b>Plaintext on purpose</b> and content-free by contract, bounded at 4 KiB
    ///     by the store: it carries service names, exit codes and failure categories, never a variable value — which is
    ///     why this family's only encrypted column is the instance's <c>variables_json</c> and this one is replayable to
    ///     the hub as it stands.
    /// </summary>
    public string? DetailJson { get; set; }

    /// <summary>Unix-ms instant the event occurred. Plaintext (structural).</summary>
    public long OccurredAtUtc { get; set; }
}
