namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentTemplatesEndpoint(IDevelopmentTemplateService service)
    : EndpointWithoutRequest<ListDevelopmentTemplatesResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var templates = await _service.ListTemplatesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentTemplatesResponse(templates.Select(template => template.ToResponse()).ToArray()), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class RegisterDevelopmentTemplateEndpoint(IDevelopmentTemplateService service)
    : Endpoint<RegisterDevelopmentTemplateRequest, DevelopmentTemplateResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RegisterDevelopmentTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var template = await _service.AddTemplateAsync(req.Alias, req.HostPath, ct).ConfigureAwait(false);
            await Send.OkAsync(template.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or DevelopmentTemplateAliasInUseException
                                              or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class RemoveDevelopmentTemplateEndpoint(IDevelopmentTemplateService service)
    : Endpoint<DevelopmentTemplateRequest>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Development.TemplateById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTemplateRequest req, CancellationToken ct)
    {
        if (!await _service.RemoveTemplateAsync(req.TemplateId, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

public sealed class CreateDevelopmentRepositoryFromTemplateEndpoint(IDevelopmentTemplateService service)
    : Endpoint<CreateDevelopmentRepositoryFromTemplateRequest, DevelopmentRepositoryFromTemplateResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

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
            var result = await _service.CreateFromTemplateAsync(req.TemplateId, req.DestinationPath, req.Alias, req.BaseBranch, ct)
                                       .ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentRepositoryFromTemplateResponse(result.Repository.ToResponse(),
                    result.TemplateAlias,
                    result.TemplateCommit),
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or DevelopmentTemplateMaterializationException
                                              or DirectoryNotFoundException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
