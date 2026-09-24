namespace XE_Local_AI_Engine.Client.Endpoints.Workspaces.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Workspace;

public sealed class CreateWorkspaceEndpoint : Endpoint<CreateWorkspaceRequest, WorkspaceResponse>
{
    private readonly ISelectedFolderResolver _selectedFolders;

    public CreateWorkspaceEndpoint(ISelectedFolderResolver selectedFolders)
    {
        ArgumentNullException.ThrowIfNull(selectedFolders);
        _selectedFolders = selectedFolders;
    }

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
        // A relative path is left to the resolver's own traversal-free check; an absolute one must name a directory that exists
        // now, or the operator only learns of the mistake inside a later agent run. The path itself is never echoed back.
        if (Path.IsPathFullyQualified(req.HostPath ?? string.Empty) && !Directory.Exists(req.HostPath))
        {
            throw new SelectedFolderValidationException("The host path does not exist or is not a directory.");
        }

        var reference = await _selectedFolders.RegisterAsync(new SelectedFolderRegistration
            {
                Alias = req.Alias ?? string.Empty,
                HostPath = req.HostPath ?? string.Empty,
                Mode = SelectedFolderMode.ReadOnlyMount
            },
            ct);

        await Send.OkAsync(ToResponse(reference), ct);
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
