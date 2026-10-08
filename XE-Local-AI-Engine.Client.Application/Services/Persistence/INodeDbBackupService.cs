namespace XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Snapshots the node database before pending schema migrations are applied. A bad Velopack-shipped migration
///     or on-disk corruption would otherwise be an unrecoverable loss of all chat history, agents, and golden
///     conversations.
/// </summary>
public interface INodeDbBackupService
{
    /// <summary>
    ///     Takes a consistent <c>VACUUM INTO</c> snapshot of the node database when — and only when — there are pending migrations, then
    ///     prunes older snapshots down to the configured retention count. A no-op when nothing is pending.
    /// </summary>
    /// <remarks>
    ///     <para> Availability over the guarantee: any backup failure is logged at Error and swallowed. It must never throw, so a backup
    ///     hiccup can never block migration or brick startup. </para>
    /// </remarks>
    Task BackupBeforeMigrationAsync(CancellationToken cancellationToken = default);

    /// <summary>The full path of the newest complete snapshot, or null when none exists; named in fatal startup messages.</summary>
    string? FindNewestSnapshot();

    /// <summary>What the pre-migration backup of this process run did.</summary>
    NodeDbAutomaticBackupStatus LastAutomaticBackup { get; }

    /// <summary>Every complete snapshot in the backups folder, newest first; empty when the folder does not exist.</summary>
    IReadOnlyList<NodeDbSnapshot> ListSnapshots();

    /// <summary>
    ///     Checks a listed snapshot and writes the marker the next start applies. Shares the one-at-a-time guard with
    ///     <see cref="CreateSnapshotAsync" />, so a snapshot and its retention prune never run while a restore is staged.
    /// </summary>
    Task<NodeDbRestoreStageResult> StageRestoreAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>The full path of the listed snapshot with this exact file name, or null when there is none.</summary>
    string? ResolveSnapshotPath(string name);

    /// <summary>
    ///     Takes a snapshot now with the same naming and retention as the pre-migration one. One at a time; failures other than the
    ///     reported statuses throw.
    /// </summary>
    Task<NodeDbSnapshotCreateResult> CreateSnapshotAsync(CancellationToken cancellationToken = default);
}
