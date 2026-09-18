namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

public sealed class ListCustomToolsEndpoint : EndpointWithoutRequest<ListCustomToolsResponse>
{
    private readonly ICustomToolService _customToolService;

    public ListCustomToolsEndpoint(ICustomToolService customToolService)
    {
        ArgumentNullException.ThrowIfNull(customToolService);
        _customToolService = customToolService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.CustomTools.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var views = await _customToolService.ListAsync(ct);
        await Send.OkAsync(new ListCustomToolsResponse
            {
                Items = views
            },
            ct);
    }
}
