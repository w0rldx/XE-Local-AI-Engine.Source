namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>Inputs for <see cref="IAgentHomeService.PrepareAsync" /> (already §7-validated by the tool handler).</summary>
internal sealed record AgentHomePrepareRequest
{
    /// <summary>The selected-folder ids the model referenced; each is resolved (existence-checked), not copied, in Marker I-pre.</summary>
    public required IReadOnlyList<string> SelectedFolderIds { get; init; }

    /// <summary>The model-requested runtime profile, or <see langword="null" /> to use the worker default.</summary>
    public string? RuntimeProfile { get; init; }
}

/// <summary>Outcome of <see cref="IAgentHomeService.PrepareAsync" />; consumed by <see cref="IAgentHomeService.RunAsync" />.</summary>
internal sealed record AgentHomePrepareResult
{
    /// <summary>The recovered worker-local layout (root + manifest).</summary>
    public required AgentHomeLayout Layout { get; init; }

    /// <summary>The live sandbox handle to run commands against.</summary>
    public required SandboxHandle Handle { get; init; }

    /// <summary>The resolved selected folders (trusted host paths; copy is Marker F, not Marker I-pre).</summary>
    public required IReadOnlyList<ResolvedSelectedFolder> ResolvedFolders { get; init; }

    /// <summary>
    ///     The model-safe per-folder copy outcome (alias + counts + sandbox-relative path) produced by the workspace
    ///     copy (Marker F). Empty when there are no selected folders.
    /// </summary>
    public required IReadOnlyList<SelectedFolderSnapshot> FolderSnapshots { get; init; }

    /// <summary>The effective runtime profile the sandbox was created with (after worker-policy resolution).</summary>
    public required string RuntimeProfile { get; init; }
}

/// <summary>Inputs for <see cref="IAgentHomeService.RunAsync" />.</summary>
internal sealed record AgentHomeRunRequest
{
    /// <summary>The completed preparation result.</summary>
    public required AgentHomePrepareResult Prepared { get; init; }

    /// <summary>The model-supplied goal (carried for logging/agent execution in later markers).</summary>
    public required string Goal { get; init; }

    /// <summary>The validated <c>allowedActions</c> the run is permitted (enforced fully in Marker I).</summary>
    public required IReadOnlyList<string> AllowedActions { get; init; }
}

/// <summary>Compact result of <see cref="IAgentHomeService.RunAsync" /> returned to the model.</summary>
internal sealed record AgentHomeRunResult
{
    /// <summary>The run id; run outputs live under <c>/agent-home/runs/&lt;run-id&gt;</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>Whether the command ran to completion (false when cancelled/killed mid-flight).</summary>
    public required bool Completed { get; init; }

    /// <summary>The command's exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>The worker-local path to the run's log directory.</summary>
    public required string LogPath { get; init; }

    /// <summary>
    ///     The patch-export outcome (Marker G): changed-file count, blocked flag, and run-relative artifact paths. When
    ///     no selected folder was copied (no git baseline) or nothing changed, the export is empty (zero changed files,
    ///     null paths).
    /// </summary>
    public required AgentHomePatchExport Patch { get; init; }
}

/// <summary>
///     Model-safe outcome of the Marker G patch export. Paths are run-relative (<c>runs/&lt;run-id&gt;/patches/…</c>),
///     never the worker-host root (AgentHome plan §9.1 / §11).
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
