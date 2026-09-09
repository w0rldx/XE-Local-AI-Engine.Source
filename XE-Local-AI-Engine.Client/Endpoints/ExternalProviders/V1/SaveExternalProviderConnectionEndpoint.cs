namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     Inserts or replaces one external connection and everything a save owes the rest of the node.
/// </summary>
/// <remarks>
///     <para>
///         It calls the ADMINISTRATION service, never the store: a bare store write would leave the connection's models
///         unroutable (no provider-map row), possibly not tool-capable (no allow-list entry), and — after an API-key or
///         base-URL edit — still being sent to with the previous values from a cached chat client.
///     </para>
///     <para>
///         The response is the whole configuration rather than the saved connection alone, because a committed write
///         moves the store revision, and an editor holding the old one would lose its next write to a 409 it could not
///         explain.
///     </para>
/// </remarks>
public sealed class SaveExternalProviderConnectionEndpoint(IExternalProviderAdministrationService administrationService)
    : Endpoint<SaveExternalProviderConnectionRequest, ExternalProviderConnectionsResponse>
{
    private readonly IExternalProviderAdministrationService _administrationService =
        administrationService ?? throw new ArgumentNullException(nameof(administrationService));

    public override void Configure()
    {
        Put(LocalApiRoutes.ExternalProviders.ConnectionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ExternalProviderConnectionsResponse>(StatusCodes.Status200OK)
                               .Produces<ExternalProviderConnectionsResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(SaveExternalProviderConnectionRequest req, CancellationToken ct)
    {
        // The store owns every storable-shape rule, so its ExternalProviderValidationException message IS the
        // operator-facing explanation, and the global DomainValidationExceptionHandler surfaces it verbatim as the
        // 400. That keeps one statement of each bound instead of a second, drifting copy in a validator.
        var result = await _administrationService.SaveConnectionAsync(req.ToSaveRequest(), ct).ConfigureAwait(false);

        await SendWriteResultAsync(result, ct).ConfigureAwait(false);
    }

    private Task SendWriteResultAsync(ExternalProviderWriteResult result, CancellationToken ct)
    {
        return result switch
        {
            ExternalProviderWriteResult.Committed committed => Send.OkAsync(committed.Config.ToResponse(), ct),

            // The caller read a revision the file has since moved past. Answering with what is ACTUALLY stored lets the
            // editor re-render the real state instead of guessing what the other writer did.
            ExternalProviderWriteResult.Superseded superseded => Send.ResultAsync(Results.Conflict(superseded.Current.ToResponse())),
            _ => throw new InvalidOperationException($"Unknown external provider write result: {result.GetType().Name}.")
        };
    }
}
