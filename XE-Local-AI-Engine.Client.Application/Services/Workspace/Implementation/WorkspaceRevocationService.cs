namespace XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class WorkspaceRevocationService(
    INodeSelectedFolderStore store,
    IWorkspaceRevocationPreparation preparation,
    ILogger<WorkspaceRevocationService> logger) : IWorkspaceRevocationService
{
    private readonly ILogger<WorkspaceRevocationService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IWorkspaceRevocationPreparation _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
    private readonly INodeSelectedFolderStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task RevokeAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        if (!Guid.TryParse(workspaceId, out var id))
        {
            throw new SelectedFolderValidationException("The workspace id is not a valid identifier.");
        }

        var record = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return;
        }

        var resolved = new ResolvedSelectedFolder(record.Id, record.Alias, record.HostPath, record.Mode);
        await using var session = await _preparation.PrepareAsync(resolved, cancellationToken).ConfigureAwait(false)
                                  ?? throw new InvalidOperationException("Workspace revocation preparation returned no lease-bearing session.");

        if (await _store.RevokeAsync(id, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Revoked selected workspace {WorkspaceId} with alias {Alias}.", record.Id, record.Alias);
        }
    }
}
