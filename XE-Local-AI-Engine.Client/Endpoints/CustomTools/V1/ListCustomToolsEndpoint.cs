namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

public sealed class ListCustomToolsEndpoint(ICustomToolService customToolService)
    : EndpointWithoutRequest<ListCustomToolsResponse>
{
    private readonly ICustomToolService _customToolService = customToolService ?? throw new ArgumentNullException(nameof(customToolService));

    public override void Configure()
    {
        Get(LocalApiRoutes.CustomTools.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var views = await _customToolService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListCustomToolsResponse
            {
                Items = views
            },
            ct).ConfigureAwait(false);
    }
}
