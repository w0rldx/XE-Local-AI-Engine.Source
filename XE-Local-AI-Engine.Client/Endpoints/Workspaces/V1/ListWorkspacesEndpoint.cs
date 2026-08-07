namespace XE_Local_AI_Engine.Client.Endpoints.Workspaces.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Workspace;

public sealed class ListWorkspacesEndpoint(ISelectedFolderResolver selectedFolders)
    : EndpointWithoutRequest<ListWorkspacesResponse>
{
    private readonly ISelectedFolderResolver _selectedFolders = selectedFolders ?? throw new ArgumentNullException(nameof(selectedFolders));

    public override void Configure()
    {
        Get(LocalApiRoutes.Workspaces.Collection);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.AutoTagOverride("Workspaces"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var references = await _selectedFolders.ListReferencesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListWorkspacesResponse
            {
                Items =
                [
                    .. references.Select(static reference => new WorkspaceResponse
                    {
                        WorkspaceId = reference.Id,
                        Alias = reference.Alias
                    })
                ]
            },
            ct).ConfigureAwait(false);
    }
}
