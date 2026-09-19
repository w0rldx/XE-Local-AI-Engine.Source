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
        await Send.OkAsync(new ListDevelopmentTemplatesResponse { Templates = templates.Select(template => template.ToResponse()).ToArray() }, ct);
    }
}

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

public sealed class CreateDevelopmentRepositoryFromTemplateEndpoint : Endpoint<CreateDevelopmentRepositoryFromTemplateRequest, DevelopmentRepositoryFromTemplateResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service;

    public CreateDevelopmentRepositoryFromTemplateEndpoint(IDevelopmentTemplateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.RepositoriesFromTemplate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateDevelopmentRepositoryFromTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _service.CreateFromTemplateAsync(req.TemplateId, req.DestinationPath, req.Alias, req.BaseBranch, ct);
            await Send.OkAsync(new DevelopmentRepositoryFromTemplateResponse
            {
                Repository = result.Repository.ToResponse(),
                TemplateAlias = result.TemplateAlias,
                TemplateCommit = result.TemplateCommit
            },
                ct);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or DirectoryNotFoundException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
