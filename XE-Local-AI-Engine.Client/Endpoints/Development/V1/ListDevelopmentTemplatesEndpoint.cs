namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentTemplatesEndpoint : EndpointWithoutRequest<ListDevelopmentTemplatesResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service;

    public ListDevelopmentTemplatesEndpoint(IDevelopmentTemplateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var templates = await _service.ListTemplatesAsync(ct);
        await Send.OkAsync(new ListDevelopmentTemplatesResponse
        {
            Templates = templates.Select(template => template.ToResponse()).ToArray()
        }, ct);
    }
}
