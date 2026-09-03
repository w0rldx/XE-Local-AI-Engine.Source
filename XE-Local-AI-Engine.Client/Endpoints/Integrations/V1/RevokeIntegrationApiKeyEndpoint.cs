namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Revokes a credential. A SOFT revoke — the row is stamped, never deleted — because execution rows and the
///     content-free audit rows reference the credential's prefix, and deleting it would orphan that history and let the
///     same display prefix be minted again.
/// </summary>
public sealed class RevokeIntegrationApiKeyEndpoint(IIntegrationApiKeyService apiKeyService)
    : EndpointWithoutRequest
{
    private readonly IIntegrationApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Integrations.KeyById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await _apiKeyService.RevokeAsync(Route<Guid>("keyId"), ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
