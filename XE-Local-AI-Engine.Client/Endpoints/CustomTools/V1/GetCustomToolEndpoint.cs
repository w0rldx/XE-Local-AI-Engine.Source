namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

public sealed class GetCustomToolEndpoint : EndpointWithoutRequest<CustomToolView>
{
    private readonly ICustomToolService _customToolService;

    public GetCustomToolEndpoint(ICustomToolService customToolService)
    {
        ArgumentNullException.ThrowIfNull(customToolService);
        _customToolService = customToolService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.CustomTools.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var customToolId = Route<Guid>("customToolId");
        var view = await _customToolService.GetByIdAsync(customToolId, ct);
        if (view is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(view, ct);
    }
}
