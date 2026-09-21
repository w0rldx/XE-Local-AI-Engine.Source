namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class RemoveDevelopmentTemplateEndpoint : Endpoint<DevelopmentTemplateRequest>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service;

    public RemoveDevelopmentTemplateEndpoint(IDevelopmentTemplateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Development.TemplateById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTemplateRequest req, CancellationToken ct)
    {
        if (!await _service.RemoveTemplateAsync(req.TemplateId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
