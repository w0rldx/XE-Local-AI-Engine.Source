namespace XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Snapshots the node database before pending schema migrations are applied (BE-06). A bad Velopack-shipped migration
///     or on-disk corruption would otherwise be an unrecoverable loss of all chat history, agents, and golden
///     conversations.
/// </summary>
public interface INodeDbBackupService
{
    /// <summary>
    ///     Takes a consistent <c>VACUUM INTO</c> snapshot of the node database when — and only when — there are pending
    ///     migrations, then prunes older snapshots down to the configured retention count. A no-op when nothing is pending.
    ///     <para>
    ///         Availability over the guarantee: any backup failure is logged at Error and swallowed. It must never throw, so a
    ///         backup hiccup can never block migration or brick startup.
    ///     </para>
    /// </summary>
    Task BackupBeforeMigrationAsync(CancellationToken cancellationToken = default);
}
