namespace XE_Local_AI_Engine.Client.Endpoints.NodeBackups.V1;

using XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>One node database snapshot, named by its file name only.</summary>
public sealed class NodeBackupResponse
{
    public required string Name { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>The outcome of this process run's pre-migration backup.</summary>
public sealed class NodeAutomaticBackupResponse
{
    /// <summary><c>NotRun</c> also covers a start with no pending migration, which needs no backup.</summary>
    public required NodeDbAutomaticBackupOutcome Outcome { get; init; }

    public DateTimeOffset? AtUtc { get; init; }

    /// <summary>Why it was skipped or failed; no path. Null otherwise.</summary>
    public string? Error { get; init; }
}

/// <summary>Response for <c>GET node/backups</c>: the snapshots newest first, and the last automatic backup.</summary>
public sealed class NodeBackupListResponse
{
    public required IReadOnlyList<NodeBackupResponse> Backups { get; init; }

    public required NodeAutomaticBackupResponse LastAutomaticBackup { get; init; }
}

/// <summary>Route request for <c>POST node/backups/{name}/restore</c>.</summary>
public sealed class RestoreNodeBackupRequest
{
    /// <summary>The snapshot file name exactly as the list returned it.</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>Response for an accepted restore: the node stops once this response completes and applies it at the next start.</summary>
public sealed class RestoreNodeBackupResponse
{
    public required bool NodeStopping { get; init; }
}
