namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     patch export patch export. After the run, diff the in-sandbox git baseline that the
///     workspace copy (workspace copy) created and write <c>changes.patch</c> + <c>changed-files.json</c> under the host-side
///     <c>runs/&lt;run-id&gt;/patches/</c> directory. The result carries run-relative paths and counts only — never a
///     host path.
/// </summary>
internal interface IAgentHomePatchService
{
    Task<AgentHomePatchExport> ExportPatchAsync(
        SandboxHandle handle,
        AgentHomePatchExportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Inputs for <see cref="IAgentHomePatchService.ExportPatchAsync" />.</summary>
internal sealed record AgentHomePatchExportRequest
{
    /// <summary>The run id; the export artifacts live under <c>runs/&lt;run-id&gt;/patches/</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>
    ///     The worker-local host run directory (<c>&lt;RootPath&gt;/runs/&lt;run-id&gt;</c>); the export writes the
    ///     <c>patches/</c> subdirectory here, a sibling of the run's <c>logs/</c>. This is the host root, not the
    ///     in-sandbox <c>/agent-home</c> (AgentHome plan two-roots split).
    /// </summary>
    public required string HostRunDirectory { get; init; }

    /// <summary>The resolved selected folders, used to map a changed <c>&lt;alias&gt;</c> back to its selected-folder id.</summary>
    public required IReadOnlyList<ResolvedSelectedFolder> ResolvedFolders { get; init; }
}
