namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Every <c>xeint_</c> credential, revoked ones included — a revoked row is history an operator still needs. No
///     response on this route can carry a secret: the node keeps only a digest.
/// </summary>
public sealed class ListIntegrationApiKeysEndpoint(IIntegrationApiKeyService apiKeyService)
    : EndpointWithoutRequest<ListIntegrationApiKeysResponse>
{
    private readonly IIntegrationApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.Keys);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var keys = await _apiKeyService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListIntegrationApiKeysResponse
            {
                Items = keys.Select(IntegrationMapper.ToView).ToArray()
            },
            ct).ConfigureAwait(false);
    }
}
