namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One persisted phase-boundary or output event on an execution's stream. A plain class rather than a record
///     class, matching the two sibling event tables it copies (<see cref="DevWorkflowRunEvent" />,
///     <see cref="AgentWorkSessionEvent" />). Only the nine allow-listed event types reach this table; per-token
///     assistant deltas never do.
/// </summary>
internal sealed class IntegrationExecutionEvent
{
    /// <summary>
    ///     A <c>Guid</c> rather than an autoincrement <c>long</c>, because the column-encryption AAD binds a
    ///     <c>Guid</c> record id: a shared surrogate would give every event on one execution the same AAD, which is the
    ///     substitution distinct AAD column names exist to prevent.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>The owning execution; also the conversation component of <see cref="DetailJson" />'s AAD.</summary>
    public Guid ExecutionId { get; set; }

    /// <summary>
    ///     Monotonic per execution, starting at 1 with <c>execution.accepted</c>. Minted by the coordinator's event
    ///     buffer and never by the store, so a duplicate <c>(execution_id, sequence)</c> surfacing as a unique-index
    ///     violation means a caller minted a duplicate — a bug, not a race to swallow. Holes are legal: a reserved
    ///     sequence whose commit failed is abandoned, and readers treat the watermark as a watermark.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>One of the nine persisted <c>IntegrationStreamEventTypes</c> constants. Plaintext (structural).</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    ///     The event's payload as UTF-8 bytes — for an <c>external.output</c> event this is the tool's payload
    ///     verbatim, up to 256 KiB, which makes it the one column in this family that holds real content. Plaintext
    ///     while tracked in memory; encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and
    ///     decrypted by <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>integration_execution_event_detail_json</c>, with the owning execution in the conversation slot so a
    ///     re-parented event row fails its tag check instead of reading back as another execution's output. Optional.
    ///     <para>
    ///         <c>length(detail_json)</c> is <b>not</b> a plaintext byte count: the stored BLOB is
    ///         <c>nonce ‖ ciphertext ‖ tag</c>. Output accounting reads <c>IntegrationExecution.OutputBytes</c>.
    ///     </para>
    /// </summary>
    public byte[]? DetailJson { get; set; }

    /// <summary>Unix-ms instant the event occurred. Plaintext (structural).</summary>
    public long OccurredAtUtc { get; set; }
}
