namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     FastEndpoints handler to set or clear the Hugging Face access token (POST model-fit/hf-token). Thin transport over
///     the Lane B <see cref="IHfTokenStore" />: a non-empty token is stored encrypted at rest; a null/empty token clears
///     the stored token (anonymous access).
///     <para>
///         <b>Secret hygiene (plan §10):</b> the token is NEVER returned by this endpoint, NEVER logged, and NEVER echoed
///         in the response. The response reports ONLY whether a token is now configured — the value itself never leaves the
///         store.
///     </para>
/// </summary>
public sealed class SetHfTokenEndpoint(IHfTokenStore tokenStore)
    : Endpoint<SetHfTokenRequest, HfTokenStatusResponse>
{
    private readonly IHfTokenStore _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.HfToken);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetHfTokenRequest req, CancellationToken ct)
    {
        // A null/whitespace token is an explicit "clear" (return to anonymous access); a non-empty token is stored
        // encrypted. The raw value is never logged or echoed — only the resulting presence flag is returned.
        if (string.IsNullOrWhiteSpace(req.Token))
        {
            await _tokenStore.ClearTokenAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(new HfTokenStatusResponse
                {
                    HasToken = false
                },
                ct).ConfigureAwait(false);
            return;
        }

        await _tokenStore.SetTokenAsync(req.Token.Trim(), ct).ConfigureAwait(false);
        await Send.OkAsync(new HfTokenStatusResponse
            {
                HasToken = true
            },
            ct).ConfigureAwait(false);
    }
}
