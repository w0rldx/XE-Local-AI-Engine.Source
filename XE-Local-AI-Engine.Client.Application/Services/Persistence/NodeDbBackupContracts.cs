namespace XE_Local_AI_Engine.Client.Services.Persistence;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>One complete node database snapshot in the backups folder, named by its file name only.</summary>
public sealed class NodeDbSnapshot
{
    public required string Name { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>What the pre-migration backup of this process run did.</summary>
public enum NodeDbAutomaticBackupOutcome
{
    /// <summary>No pre-migration backup ran in this process, or nothing was pending so none was needed.</summary>
    NotRun,
    Succeeded,

    /// <summary>The free-space guard or the time budget refused the snapshot.</summary>
    Skipped,
    Failed
}

/// <summary>The outcome of the pre-migration backup, held in memory for this process run.</summary>
public sealed class NodeDbAutomaticBackupStatus
{
    public static readonly NodeDbAutomaticBackupStatus NotRun = new()
    {
        Outcome = NodeDbAutomaticBackupOutcome.NotRun
    };

    public required NodeDbAutomaticBackupOutcome Outcome { get; init; }

    public DateTimeOffset? AtUtc { get; init; }

    /// <summary>Why the backup was skipped or failed; carries no path. Null otherwise.</summary>
    public string? Error { get; init; }
}

/// <summary>The result of an on-demand snapshot: the new row when it was created, otherwise why not.</summary>
public abstract record NodeDbSnapshotCreateResult
{
    public sealed record Created : NodeDbSnapshotCreateResult
    {
        public required NodeDbSnapshot Snapshot { get; init; }
    }

    /// <summary>Another on-demand snapshot is running.</summary>
    public sealed record Busy : NodeDbSnapshotCreateResult;

    /// <summary>The node database is not a file this node can snapshot.</summary>
    public sealed record Unsupported : NodeDbSnapshotCreateResult;

    public sealed record InsufficientSpace : NodeDbSnapshotCreateResult;
}

/// <summary>What <see cref="NodeDbRestoreStaging.ApplyPendingAsync" /> did; <see cref="Error" /> is set when a staged restore failed.</summary>
public sealed class NodeDbRestoreApplyResult
{
    public static readonly NodeDbRestoreApplyResult None = new()
    {
        Applied = false
    };

    public required bool Applied { get; init; }

    /// <summary>True once the snapshot is the live database, also when the marker outlived it and the start must stop.</summary>
    public bool Installed { get; init; }

    public string? SnapshotName { get; init; }

    /// <summary>Where the replaced database was moved; null when there was none.</summary>
    public string? SetAsidePath { get; init; }

    public string? MarkerPath { get; init; }

    public string? Error { get; init; }

    /// <summary>Set when the restore was applied but something after the install went wrong; the start continues.</summary>
    public string? Warning { get; init; }
}

public enum NodeDbRestoreStageStatus
{
    Staged,

    /// <summary>A snapshot is being taken; staging waits for it.</summary>
    Busy,

    /// <summary>The snapshot cannot be restored; <see cref="NodeDbRestoreStageResult.Reason" /> says why.</summary>
    Refused
}

public sealed class NodeDbRestoreStageResult
{
    public required NodeDbRestoreStageStatus Status { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
///     Thrown when an operator names a backup that is not a listed snapshot. <c>ResourceNotFoundExceptionHandler</c>
///     answers it with the bodyless 404.
/// </summary>
public sealed class NodeBackupNotFoundException : ResourceNotFoundException
{
    public NodeBackupNotFoundException(string name) : base($"Backup '{name}' was not found.")
    {
    }
}
