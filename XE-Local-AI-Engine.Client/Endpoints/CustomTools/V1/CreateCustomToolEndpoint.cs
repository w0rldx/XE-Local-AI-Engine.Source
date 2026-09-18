namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

public sealed class CreateCustomToolEndpoint : Endpoint<CustomToolDefinition, CustomToolView>
{
    private readonly ICustomToolService _customToolService;

    public CreateCustomToolEndpoint(ICustomToolService customToolService)
    {
        ArgumentNullException.ThrowIfNull(customToolService);
        _customToolService = customToolService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.CustomTools.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CustomToolDefinition req, CancellationToken ct)
    {
        var view = await _customToolService.CreateAsync(req, ct);
        await Send.CreatedAtAsync<GetCustomToolEndpoint>(new
            {
                customToolId = view.Id
            },
            view,
            cancellation: ct);
    }
}
