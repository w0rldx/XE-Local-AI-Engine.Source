namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler reporting whether a Hugging Face access token is configured (GET model-fit/hf-token), a
///     thin transport over the token store (<see cref="IHfTokenStore.HasTokenAsync" />).
/// </summary>
/// <remarks>
///     <b>Secret hygiene:</b> it returns ONLY a boolean presence flag — it NEVER returns or logs the token value, which
///     never leaves the encrypted store.
/// </remarks>
public sealed class GetHfTokenStatusEndpoint : EndpointWithoutRequest<HfTokenStatusResponse>
{
    private readonly IHfTokenStore _tokenStore;

    public GetHfTokenStatusEndpoint(IHfTokenStore tokenStore)
    {
        ArgumentNullException.ThrowIfNull(tokenStore);
        _tokenStore = tokenStore;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.HfToken);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var hasToken = await _tokenStore.HasTokenAsync(ct);
        await Send.OkAsync(new HfTokenStatusResponse
            {
                HasToken = hasToken
            },
            ct);
    }
}
