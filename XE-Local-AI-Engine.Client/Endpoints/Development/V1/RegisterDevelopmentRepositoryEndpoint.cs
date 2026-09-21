namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class RegisterDevelopmentRepositoryEndpoint : Endpoint<RegisterDevelopmentRepositoryRequest, DevelopmentRepositoryResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public RegisterDevelopmentRepositoryEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RegisterDevelopmentRepositoryRequest req, CancellationToken ct)
    {
        try
        {
            var repository = await _service.RegisterRepositoryAsync(req.Alias, req.HostPath, ct);
            await Send.OkAsync(repository.ToResponse(), ct);
        }
        catch (Exception exception) when (exception is ArgumentException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
