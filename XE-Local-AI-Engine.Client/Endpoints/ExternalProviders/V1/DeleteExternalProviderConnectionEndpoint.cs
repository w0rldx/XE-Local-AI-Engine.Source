namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     Removes one external connection, its models' routing, its tool allow-list entries, and — when one of its models
///     was the node default — that default.
/// </summary>
/// <remarks>
///     Deleting a connection that is already gone succeeds with no change, so a retry after a partial failure is not an
///     error. The response is the whole configuration for the same reason the save's is: the revision has moved.
/// </remarks>
public sealed class DeleteExternalProviderConnectionEndpoint(IExternalProviderAdministrationService administrationService)
    : Endpoint<DeleteExternalProviderConnectionRequest, ExternalProviderConnectionsResponse>
{
    private readonly IExternalProviderAdministrationService _administrationService =
        administrationService ?? throw new ArgumentNullException(nameof(administrationService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.ExternalProviders.ConnectionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ExternalProviderConnectionsResponse>(StatusCodes.Status200OK)
                               .Produces<ExternalProviderConnectionsResponse>(StatusCodes.Status409Conflict)
                               // Declared because it is reachable: a malformed connection id, and a store this build
                               // must not write (unreadable, or written by a newer version), both refuse with a 400.
                               // An undeclared status is one the generated client cannot model.
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(DeleteExternalProviderConnectionRequest req, CancellationToken ct)
    {
        ExternalProviderWriteResult result;
        try
        {
            result = await _administrationService
                           .DeleteConnectionAsync(req.ConnectionId ?? string.Empty, req.ExpectedRevision, ct)
                           .ConfigureAwait(false);
        }
        catch (ExternalProviderValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        switch (result)
        {
            case ExternalProviderWriteResult.Committed committed:
                await Send.OkAsync(committed.Config.ToResponse(), ct).ConfigureAwait(false);
                return;
            case ExternalProviderWriteResult.Superseded superseded:
                await Send.ResultAsync(Results.Conflict(superseded.Current.ToResponse())).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"Unknown external provider write result: {result.GetType().Name}.");
        }
    }
}
