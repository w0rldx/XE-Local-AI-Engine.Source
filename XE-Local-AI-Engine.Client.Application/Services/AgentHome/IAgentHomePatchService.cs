namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Patch export: after the run, diff the in-sandbox git baseline that the workspace copy created and write
///     <c>changes.patch</c> and <c>changed-files.json</c> under the host-side <c>runs/&lt;run-id&gt;/patches/</c>.
/// </summary>
/// <remarks>
///     The result carries run-relative paths and counts only — never a host path.
/// </remarks>
internal interface IAgentHomePatchService
{
    Task<AgentHomePatchExport> ExportPatchAsync(SandboxHandle handle,
        AgentHomePatchExportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Inputs for <see cref="IAgentHomePatchService.ExportPatchAsync" />.</summary>
internal sealed record AgentHomePatchExportRequest
{
    /// <summary>The run id; the export artifacts live under <c>runs/&lt;run-id&gt;/patches/</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>
    ///     The worker-local host run directory (<c>&lt;RootPath&gt;/runs/&lt;run-id&gt;</c>), under which the export
    ///     writes the <c>patches/</c> subdirectory, a sibling of the run's <c>logs/</c>.
    /// </summary>
    /// <remarks>
    ///     This is the host root, not the in-sandbox <c>/agent-home</c> — the two-root host/sandbox split.
    /// </remarks>
    public required string HostRunDirectory { get; init; }

    /// <summary>The resolved selected folders, used to map a changed <c>&lt;alias&gt;</c> back to its selected-folder id.</summary>
    public required IReadOnlyList<ResolvedSelectedFolder> ResolvedFolders { get; init; }

    /// <summary>
    ///     The run's logger, to which export's git commands are appended in <c>commands.jsonl</c> beside the model's
    ///     own, attributed to the node.
    /// </summary>
    /// <remarks>
    ///     They run on the same workspace the model just had write and command access to, after its turn ended, so an
    ///     audit that could not see them would be missing the most interesting entries.
    /// </remarks>
    public required IAgentHomeRunLogger RunLogger { get; init; }
}
