namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler to set or clear the Hugging Face access token (POST model-fit/hf-token), a thin transport
///     over the token store (<see cref="IHfTokenStore" />).
/// </summary>
/// <remarks>
///     A non-empty token is stored encrypted at rest; a null/empty one clears it (anonymous access).
///     <b>Secret hygiene:</b> the token is NEVER returned by this endpoint, NEVER logged and NEVER echoed in the
///     response, which reports ONLY whether a token is now configured — the value itself never leaves the store.
/// </remarks>
public sealed class SetHfTokenEndpoint : Endpoint<SetHfTokenRequest, HfTokenStatusResponse>
{
    private readonly IHfTokenStore _tokenStore;

    public SetHfTokenEndpoint(IHfTokenStore tokenStore)
    {
        ArgumentNullException.ThrowIfNull(tokenStore);
        _tokenStore = tokenStore;
    }

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
            await _tokenStore.ClearTokenAsync(ct);
            await Send.OkAsync(new HfTokenStatusResponse
                {
                    HasToken = false
                },
                ct);
            return;
        }

        await _tokenStore.SetTokenAsync(req.Token.Trim(), ct);
        await Send.OkAsync(new HfTokenStatusResponse
            {
                HasToken = true
            },
            ct);
    }
}
