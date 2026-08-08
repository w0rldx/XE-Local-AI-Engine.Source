namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Durable node-local key/value flags for one-shot database maintenance that must survive a restart — currently the
///     content-encryption backfill's "plaintext residue reclamation still owed" marker. Not encrypted: a task name and a
///     presence flag are not secrets. Kept as its own tiny table so a maintenance flag stays consistent with the data it
///     guards and is preserved verbatim by <c>VACUUM</c>.
/// </summary>
internal sealed record class ChatMaintenanceState
{
    /// <summary>Maintenance task key (primary key).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Opaque flag value; <c>"1"</c> while the task is pending. The row's absence means "not pending".</summary>
    public string Value { get; set; } = string.Empty;
}
