namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

public sealed class DeleteCustomToolEndpoint : EndpointWithoutRequest
{
    private readonly ICustomToolService _customToolService;

    public DeleteCustomToolEndpoint(ICustomToolService customToolService)
    {
        ArgumentNullException.ThrowIfNull(customToolService);
        _customToolService = customToolService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.CustomTools.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var customToolId = Route<Guid>("customToolId");
        var deleted = await _customToolService.DeleteAsync(customToolId, ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
