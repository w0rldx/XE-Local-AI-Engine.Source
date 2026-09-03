namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     Mints a credential and returns the plaintext ONCE. Every later read returns prefix, label and timestamps only —
///     the node stores a digest, so a key not captured here is unrecoverable and the operator must generate another.
///     <para>
///         Supplying <c>PrincipalId</c> ROTATES a credential for an existing integrator: the new key inherits the
///         sessions and in-flight executions the old one owned.
///     </para>
/// </summary>
public sealed class GenerateIntegrationApiKeyEndpoint(IIntegrationApiKeyService apiKeyService)
    : Endpoint<GenerateIntegrationApiKeyRequest, GenerateIntegrationApiKeyResponse>
{
    private readonly IIntegrationApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Integrations.Keys);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GenerateIntegrationApiKeyRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var generated = await _apiKeyService.GenerateAsync(req.Label, req.AllowedTriggerIds, req.PrincipalId, ct).ConfigureAwait(false);

        await Send.OkAsync(new GenerateIntegrationApiKeyResponse
            {
                Key = generated.Key,
                View = IntegrationMapper.ToView(generated.View)
            },
            ct).ConfigureAwait(false);
    }
}
