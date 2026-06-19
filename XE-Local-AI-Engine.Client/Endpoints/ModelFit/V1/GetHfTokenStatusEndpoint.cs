namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     FastEndpoints handler reporting whether a Hugging Face access token is configured (GET model-fit/hf-token). Thin
///     transport over the Hugging Face token store (<see cref="IHfTokenStore.HasTokenAsync" />).
///     <para>
///         <b>Secret hygiene:</b> this endpoint returns ONLY a boolean presence flag — it NEVER returns or logs
///         the token value. The token never leaves the encrypted store.
///     </para>
/// </summary>
public sealed class GetHfTokenStatusEndpoint(IHfTokenStore tokenStore)
    : EndpointWithoutRequest<HfTokenStatusResponse>
{
    private readonly IHfTokenStore _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.HfToken);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var hasToken = await _tokenStore.HasTokenAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new HfTokenStatusResponse
            {
                HasToken = hasToken
            },
            ct).ConfigureAwait(false);
    }
}
