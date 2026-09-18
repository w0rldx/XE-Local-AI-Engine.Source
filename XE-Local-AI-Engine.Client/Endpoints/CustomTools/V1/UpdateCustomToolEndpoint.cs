namespace XE_Local_AI_Engine.Client.Endpoints.CustomTools.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;

public sealed class UpdateCustomToolEndpoint : Endpoint<CustomToolDefinition, CustomToolView>
{
    private readonly ICustomToolService _customToolService;

    public UpdateCustomToolEndpoint(ICustomToolService customToolService)
    {
        ArgumentNullException.ThrowIfNull(customToolService);
        _customToolService = customToolService;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.CustomTools.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CustomToolDefinition req, CancellationToken ct)
    {
        // The id travels in the route; the body carries the full replacement definition. A masked secret round-tripped
        // in the body resolves back to the stored value in the service, so an unrelated edit never clears a secret.
        var customToolId = Route<Guid>("customToolId");
        var view = await _customToolService.UpdateAsync(customToolId, req, ct);
        if (view is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(view, ct);
    }
}
