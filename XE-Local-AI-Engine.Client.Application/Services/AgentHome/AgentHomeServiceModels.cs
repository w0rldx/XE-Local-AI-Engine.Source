namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>Internal preparation inputs for the lease-owned AgentHome lifecycle.</summary>
internal sealed record AgentHomePrepareRequest
{
    /// <summary>The selected-folder ids the model referenced; each is resolved (existence-checked), not copied, in AgentHome gateway.</summary>
    public required IReadOnlyList<string> SelectedFolderIds { get; init; }

    /// <summary>The model-requested runtime profile, or <see langword="null" /> to use the worker default.</summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    ///     The active conversation id (seeded ambiently, NOT model-supplied) whose uploaded attachments are staged into
    ///     the sandbox as a synthetic read-only folder. <see langword="null" /> when no conversation context was seeded.
    /// </summary>
    public Guid? ConversationId { get; init; }
}

/// <summary>Internal preparation outcome consumed before the lifecycle lease is released.</summary>
internal sealed record AgentHomePrepareResult
{
    /// <summary>The recovered worker-local layout (root + manifest).</summary>
    public required AgentHomeLayout Layout { get; init; }

    /// <summary>The live sandbox handle to run commands against.</summary>
    public required SandboxHandle Handle { get; init; }

    /// <summary>The resolved selected folders (trusted host paths; copy is workspace copy, not AgentHome gateway).</summary>
    public required IReadOnlyList<ResolvedSelectedFolder> ResolvedFolders { get; init; }

    /// <summary>
    ///     The model-safe per-folder copy outcome (alias + counts + sandbox-relative path) produced by the workspace
    ///     copy (workspace copy). Empty when there are no selected folders.
    /// </summary>
    public required IReadOnlyList<SelectedFolderSnapshot> FolderSnapshots { get; init; }

    /// <summary>The effective runtime profile the sandbox was created with (after worker-policy resolution).</summary>
    public required string RuntimeProfile { get; init; }

    /// <summary>
    ///     The workspace-relative paths of the conversation attachments staged this prepare (e.g.
    ///     <c>attachments/report.md</c>), in staging order. Empty when no conversation attachments were staged. The chat
    ///     agent-mode path surfaces these to the model so a weak model reads the exact staged file instead of guessing.
    /// </summary>
    public IReadOnlyList<string> StagedAttachmentRelativePaths { get; init; } = [];
}

/// <summary>Internal run-phase inputs for the lease-owned AgentHome lifecycle.</summary>
internal sealed record AgentHomeRunRequest
{
    /// <summary>The completed preparation result.</summary>
    public required AgentHomePrepareResult Prepared { get; init; }

    /// <summary>The model-supplied goal (carried for logging and agent execution).</summary>
    public required string Goal { get; init; }

    /// <summary>The validated <c>allowedActions</c> the run is permitted (enforced fully in AgentHome gateway).</summary>
    public required IReadOnlyList<string> AllowedActions { get; init; }
}

/// <summary>
///     Inputs for <see cref="IAgentHomeService.RunLifecycleAsync" /> — the single gateway entry that wraps Prepare+Run
///     under the run-level busy guard. Carries everything the two phases need so the gateway no longer calls Prepare and
///     Run separately (which could let two prepares race before the first run acquired the guard).
/// </summary>
internal sealed record AgentHomeRunLifecycleRequest
{
    /// <summary>The selected-folder ids to resolve and copy into the workspace.</summary>
    public required IReadOnlyList<string> SelectedFolderIds { get; init; }

    /// <summary>The model-requested runtime profile, or <see langword="null" /> to use the worker default.</summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    ///     The active conversation id (seeded ambiently, NOT model-supplied) whose uploaded attachments are staged into
    ///     the sandbox. Forwarded into <see cref="AgentHomePrepareRequest" />. <see langword="null" /> when none seeded.
    /// </summary>
    public Guid? ConversationId { get; init; }

    /// <summary>The model-supplied goal carried into the run for logging.</summary>
    public required string Goal { get; init; }

    /// <summary>The validated <c>allowedActions</c> the run is permitted (gates optional G/H steps).</summary>
    public required IReadOnlyList<string> AllowedActions { get; init; }
}

/// <summary>Compact lifecycle result returned to the model.</summary>
internal sealed record AgentHomeRunResult
{
    /// <summary>The run id; run outputs live under <c>/agent-home/runs/&lt;run-id&gt;</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>Whether the command ran to completion (false when cancelled/killed mid-flight).</summary>
    public required bool Completed { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the run hit the command timeout (<see cref="AgentHomeOptions.CommandTimeoutSeconds" />)
    ///     rather than completing or being cancelled by the caller. A timeout is surfaced as a non-throwing result so the
    ///     conversation continues; a caller/connection cancel propagates <see cref="OperationCanceledException" /> instead.
    /// </summary>
    public bool TimedOut { get; init; }

    public required int ExitCode { get; init; }

    /// <summary>The worker-local path to the run's log directory.</summary>
    public required string LogPath { get; init; }

    /// <summary>
    ///     The model-safe per-folder copy outcome (alias + counts) from preparation (workspace copy), carried onto the result
    ///     so <see cref="IAgentHomeService.RunLifecycleAsync" /> callers can render the workspace summary without a
    ///     separate prepare handle. Empty when there were no selected folders.
    /// </summary>
    public IReadOnlyList<SelectedFolderSnapshot> FolderSnapshots { get; init; } = [];

    /// <summary>
    ///     The patch-export outcome (patch export): changed-file count, blocked flag, and run-relative artifact paths. When
    ///     no selected folder was copied (no git baseline) or nothing changed, the export is empty (zero changed files,
    ///     null paths).
    /// </summary>
    public required AgentHomePatchExport Patch { get; init; }
}

/// <summary>
///     Model-safe outcome of patch export. Paths are run-relative (<c>runs/&lt;run-id&gt;/patches/…</c>),
///     never the worker-host root.
/// </summary>
internal sealed record AgentHomePatchExport
{
    /// <summary>The number of changed files detected against the workspace git baseline.</summary>
    public required int ChangedFileCount { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the patch exceeded <see cref="AgentHomeOptions.MaxPatchBytes" />; the
    ///     <c>changed-files.json</c> metadata is still written but <c>changes.patch</c> is not.
    /// </summary>
    public required bool Blocked { get; init; }

    /// <summary>
    ///     <see langword="true" /> when a diff command did not complete or returned a non-zero exit code, so no patch
    ///     could be produced. Distinguishes a genuine export failure from a clean zero-change run; no artifacts are
    ///     written in this case.
    /// </summary>
    public bool Failed { get; init; }

    /// <summary>The size in bytes of the captured patch (the would-be <c>changes.patch</c> content).</summary>
    public required long PatchBytes { get; init; }

    /// <summary>Run-relative path to <c>changes.patch</c>, or <see langword="null" /> when blocked or empty.</summary>
    public string? PatchRelativePath { get; init; }

    /// <summary>Run-relative path to <c>changed-files.json</c>, or <see langword="null" /> when there were no changes.</summary>
    public string? ChangedFilesRelativePath { get; init; }
}

/// <summary>
///     Thrown when an AgentHome request is rejected by worker policy before any provider call (e.g. a runtime profile
///     the worker does not enable). The gateway maps it to a compact model-facing rejection.
/// </summary>
internal sealed class AgentHomeRequestRejectedException : InvalidOperationException
{
    public AgentHomeRequestRejectedException(string message)
        : base(message)
    {
    }

    public AgentHomeRequestRejectedException()
    {
    }

    public AgentHomeRequestRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Thrown when an AgentHome run is requested while another operation holds the owner-node execution lease. The lease
///     rejects rather than queues; the gateway maps it to a compact
///     model-facing "already in progress" rejection.
/// </summary>
internal sealed class AgentHomeBusyException : InvalidOperationException
{
    public AgentHomeBusyException(string message)
        : base(message)
    {
    }

    public AgentHomeBusyException()
    {
    }

    public AgentHomeBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
