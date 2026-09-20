namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Copies already-resolved selected folders into the sandbox workspace, called by the internal preparation phase
///     after the sandbox is attached.
/// </summary>
/// <remarks>
///     It takes trusted <see cref="ResolvedSelectedFolder" />s rather than ids because the gateway owns the resolver
///     scope. The service first replaces the selected root (including for an empty list), applies sensitive-file
///     exclusions, rejects symlink escape, enforces the per-folder byte budget, and creates a temporary in-sandbox
///     git baseline after the copy.
/// </remarks>
internal interface IAgentHomeWorkspaceService
{
    Task<IReadOnlyList<SelectedFolderSnapshot>> PrepareSelectedFoldersAsync(SandboxHandle handle,
        IReadOnlyList<ResolvedSelectedFolder> resolvedFolders,
        CancellationToken cancellationToken = default);
}
