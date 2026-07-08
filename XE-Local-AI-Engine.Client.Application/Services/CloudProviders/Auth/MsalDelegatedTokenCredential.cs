namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;

/// <summary>
///     Adapts a delegated MSAL confidential-client silent token acquisition
///     (<see cref="IClientApplicationBase.AcquireTokenSilent(IEnumerable{string}, IAccount)" />) to the Azure SDK's
///     <see cref="TokenCredential" /> contract so it plugs into <see cref="Implementation.EntraBearerTokenPipelinePolicy" />
///     unchanged — the same policy the device-code / interactive-browser / client-secret credentials use. Never
///     prompts interactively: a silent-refresh failure (<see cref="MsalUiRequiredException" />, e.g. the refresh
///     token expired or consent was revoked) surfaces as <see cref="CredentialUnavailableException" /> so the
///     caller's <c>AuthRequired</c> translation applies uniformly with the other Entra ID credential shapes.
/// </summary>
internal sealed class MsalDelegatedTokenCredential : TokenCredential
{
    private readonly IAccount _account;
    private readonly IConfidentialClientApplication _confidentialClientApplication;
    private readonly string[] _scopes;

    public MsalDelegatedTokenCredential(IConfidentialClientApplication confidentialClientApplication, IAccount account, string scope)
    {
        ArgumentNullException.ThrowIfNull(confidentialClientApplication);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        _confidentialClientApplication = confidentialClientApplication;
        _account = account;
        _scopes = [scope];
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _confidentialClientApplication.AcquireTokenSilent(_scopes, _account)
                                                               .ExecuteAsync(cancellationToken)
                                                               .ConfigureAwait(false);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException exception)
        {
            throw new CredentialUnavailableException(
                "Authorization-code sign-in has expired or requires re-consent for this connection; sign in again via Cloud Settings.", exception);
        }
    }
}
