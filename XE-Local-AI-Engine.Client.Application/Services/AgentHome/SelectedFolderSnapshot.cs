namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Outcome of copying one selected folder into the sandbox workspace. Model-safe:
///     it carries the alias, copy counts, and the sandbox-relative workspace path only — never the trusted host path
///     . The gateway renders these for the model; <see cref="IAgentHomeService.PrepareAsync" />
///     attaches them to <see cref="AgentHomePrepareResult.FolderSnapshots" />.
/// </summary>
internal sealed record SelectedFolderSnapshot
{
    /// <summary>The safe, normalized folder alias the model referenced.</summary>
    public required string Alias { get; init; }

    /// <summary>Whether the folder was copied or blocked (e.g. over the per-folder byte budget).</summary>
    public required SelectedFolderCopyStatus Status { get; init; }

    /// <summary>The number of files copied into the sandbox workspace.</summary>
    public required int CopiedFileCount { get; init; }

    /// <summary>The number of files skipped by the sensitive-file exclusion rules.</summary>
    public required int ExcludedFileCount { get; init; }

    /// <summary>The number of directories pruned (not descended into) by the exclusion rules.</summary>
    public required int ExcludedDirectoryCount { get; init; }

    /// <summary>The total bytes copied into the sandbox workspace for this folder.</summary>
    public required long CopiedBytes { get; init; }

    /// <summary>
    ///     The sandbox-relative workspace path the folder was copied to (e.g. <c>workspace/selected/&lt;alias&gt;</c>).
    ///     Never the worker host path.
    /// </summary>
    public required string WorkspacePath { get; init; }
}

/// <summary>The result of attempting to copy one selected folder into the sandbox workspace (workspace copy).</summary>
internal enum SelectedFolderCopyStatus
{
    /// <summary>The folder's surviving files were copied into the sandbox workspace.</summary>
    Copied,

    /// <summary>The folder exceeded <see cref="AgentHomeOptions.MaxSelectedFolderBytes" /> and was not copied.</summary>
    BlockedQuota
}
