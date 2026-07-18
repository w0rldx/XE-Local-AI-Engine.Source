namespace XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Options for the pre-migration node database snapshot (BE-06). Bound from the <c>NodeDbBackup</c> configuration
///     section and validated on start.
/// </summary>
public sealed class NodeDbBackupOptions
{
    public const string SectionName = "NodeDbBackup";

    /// <summary>
    ///     Optional absolute override for the directory the snapshots are written to. When null/blank the snapshots go under
    ///     <c>&lt;INodeDataDirectory.Root&gt;/backups</c>, so on a self-contained desktop build they land in the per-user data
    ///     directory rather than the (potentially read-only) install directory.
    /// </summary>
    public string? BackupDirectory { get; set; }

    /// <summary>
    ///     How many of the most recent snapshots to keep. Older snapshots are pruned after each successful backup. Must be at
    ///     least one.
    /// </summary>
    public int RetainCount { get; set; } = 3;
}
