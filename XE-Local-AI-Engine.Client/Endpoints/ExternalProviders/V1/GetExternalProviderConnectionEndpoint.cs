namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Reads one configured connection by its slug. A slug that is not stored — or one that is not even a well-formed
///     slug — is a clean 404: both mean "no such connection", and telling them apart would only report the grammar
///     back to a caller that cannot act on the difference.
/// </summary>
public sealed class GetExternalProviderConnectionEndpoint(IExternalProviderStore store)
    : Endpoint<GetExternalProviderConnectionRequest, ExternalProviderConnectionResponse>
{
    private readonly IExternalProviderStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalProviders.ConnectionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ExternalProviderConnectionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GetExternalProviderConnectionRequest req, CancellationToken ct)
    {
        // Canonicalized with the SAME helper the store mints slugs through, so a differently-cased id in the URL
        // resolves to the connection it names rather than to nothing.
        var connectionId = ExternalModelId.CanonicalizeConnectionId(req.ConnectionId);
        var config = await _store.LoadAsync(ct).ConfigureAwait(false);
        var connection = config.Connections.FirstOrDefault(candidate => string.Equals(candidate.Id, connectionId, StringComparison.Ordinal));
        if (connection is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(connection.ToResponse(), ct).ConfigureAwait(false);
    }
}
