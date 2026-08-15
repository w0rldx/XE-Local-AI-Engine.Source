namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

public sealed class CreateCustomToolEndpoint(ICustomToolService customToolService)
    : Endpoint<CustomToolDefinition, CustomToolView>
{
    private readonly ICustomToolService _customToolService = customToolService ?? throw new ArgumentNullException(nameof(customToolService));

    public override void Configure()
    {
        Post(LocalApiRoutes.CustomTools.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CustomToolDefinition req, CancellationToken ct)
    {
        var view = await _customToolService.CreateAsync(req, ct).ConfigureAwait(false);
        await Send.CreatedAtAsync<GetCustomToolEndpoint>(new
            {
                customToolId = view.Id
            },
            view,
            cancellation: ct).ConfigureAwait(false);
    }
}
