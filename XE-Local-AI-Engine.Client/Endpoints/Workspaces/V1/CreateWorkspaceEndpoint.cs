namespace XE_Local_AI_Engine.Client.Endpoints.Workspaces.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Workspace;

public sealed class CreateWorkspaceEndpoint(ISelectedFolderResolver selectedFolders)
    : Endpoint<CreateWorkspaceRequest, WorkspaceResponse>
{
    private readonly ISelectedFolderResolver _selectedFolders = selectedFolders ?? throw new ArgumentNullException(nameof(selectedFolders));

    public override void Configure()
    {
        Post(LocalApiRoutes.Workspaces.Collection);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                                   .Produces(StatusCodes.Status404NotFound)
                                                   .ProducesProblemDetails(StatusCodes.Status409Conflict)
                                                   .AutoTagOverride("Workspaces"));
    }

    public override async Task HandleAsync(CreateWorkspaceRequest req, CancellationToken ct)
    {
        try
        {
            var reference = await _selectedFolders.RegisterAsync(new SelectedFolderRegistration(req.Alias ?? string.Empty,
                    req.HostPath ?? string.Empty,
                    SelectedFolderMode.ReadOnlyMount),
                ct).ConfigureAwait(false);

            await Send.OkAsync(ToResponse(reference), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
    }

    private static WorkspaceResponse ToResponse(SelectedFolderReference reference)
    {
        return new WorkspaceResponse
        {
            WorkspaceId = reference.Id,
            Alias = reference.Alias
        };
    }
}
