namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Copies already-resolved selected folders into the sandbox workspace (AgentHome plan §6.3, Marker F). Called by
///     <see cref="IAgentHomeService.PrepareAsync" /> after the sandbox is attached. It takes trusted
///     <see cref="ResolvedSelectedFolder" />s (not ids) because preparation resolves them first to keep unknown-id
///     rejection ahead of any provider call (AgentHome plan §11) and to reuse the single resolver scope. The service
///     applies the sensitive-file exclusion rules, rejects symlink escape, enforces the per-folder byte budget, and
///     creates a temporary in-sandbox git baseline after copy (the diff base for patch export in Marker G).
/// </summary>
internal interface IAgentHomeWorkspaceService
{
    Task<IReadOnlyList<SelectedFolderSnapshot>> PrepareSelectedFoldersAsync(
        SandboxHandle handle,
        IReadOnlyList<ResolvedSelectedFolder> resolvedFolders,
        CancellationToken cancellationToken = default);
}
