namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class RegisterDevelopmentTemplateEndpoint : Endpoint<RegisterDevelopmentTemplateRequest, DevelopmentTemplateResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service;

    public RegisterDevelopmentTemplateEndpoint(IDevelopmentTemplateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RegisterDevelopmentTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var template = await _service.AddTemplateAsync(req.Alias, req.HostPath, ct);
            await Send.OkAsync(template.ToResponse(), ct);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
