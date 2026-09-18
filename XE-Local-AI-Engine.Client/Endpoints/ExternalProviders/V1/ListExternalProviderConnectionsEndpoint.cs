namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     Lists every configured external OpenAI-compatible connection and its registered models, with the store revision
///     the editor sends back on its next write. API keys are never included — only <c>hasApiKey</c>.
/// </summary>
public sealed class ListExternalProviderConnectionsEndpoint : EndpointWithoutRequest<ExternalProviderConnectionsResponse>
{
    private readonly IExternalProviderStore _store;

    public ListExternalProviderConnectionsEndpoint(IExternalProviderStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalProviders.Connections);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var config = await _store.LoadAsync(ct);
        await Send.OkAsync(config.ToResponse(), ct);
    }
}
