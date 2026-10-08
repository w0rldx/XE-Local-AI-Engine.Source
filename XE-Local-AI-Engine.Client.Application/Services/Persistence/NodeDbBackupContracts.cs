namespace XE_Local_AI_Engine.Client.Services.Persistence;

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

public enum NodeDbSnapshotCreateStatus
{
    Created,

    /// <summary>Another on-demand snapshot is running.</summary>
    Busy,

    /// <summary>The node database is not a file this node can snapshot.</summary>
    Unsupported,
    InsufficientSpace
}

/// <summary>The result of an on-demand snapshot; <see cref="Snapshot" /> is set only when it was created.</summary>
public sealed class NodeDbSnapshotCreateResult
{
    public required NodeDbSnapshotCreateStatus Status { get; init; }

    public NodeDbSnapshot? Snapshot { get; init; }
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
    NotFound,

    /// <summary>The snapshot cannot be restored; <see cref="NodeDbRestoreStageResult.Reason" /> says why.</summary>
    Refused
}

public sealed class NodeDbRestoreStageResult
{
    public required NodeDbRestoreStageStatus Status { get; init; }

    public string? Reason { get; init; }
}
