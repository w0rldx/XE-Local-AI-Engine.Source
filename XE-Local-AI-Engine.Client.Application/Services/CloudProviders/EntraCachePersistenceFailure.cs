namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Identity.Client.Extensions.Msal;

/// <summary>
///     Detects an MSAL.NET token-cache persistence failure (DPAPI / Keychain / libsecret unavailable) anywhere in an
///     exception's <see cref="Exception.InnerException" /> chain, not just at the top level.
/// </summary>
/// <remarks>
///     On a Linux box with no <c>org.freedesktop.secrets</c> provider (e.g. WSL2 without gnome-keyring/kwallet),
///     Azure.Identity's <c>DeviceCodeCredential</c> / <c>InteractiveBrowserCredential</c> do not reliably surface
///     this as their own <see cref="Azure.Identity.CredentialUnavailableException" /> — it can arrive instead as
///     <c>AuthenticationFailedException</c> wrapping <see cref="MsalCachePersistenceException" /> several levels
///     deep (live-confirmed on WSL2, 2026-07: POST cloud-settings/entra/device-code/start returned an unhandled
///     500 because the existing fallback only caught <see cref="Azure.Identity.CredentialUnavailableException" />).
///     Every no-persistence-retry fallback in this codebase checks BOTH this method AND
///     <see cref="Azure.Identity.CredentialUnavailableException" /> — see
///     <see cref="Auth.EntraDeviceCodeSignInCoordinator" />, <see cref="Implementation.AzureFoundryChatClientFactory" />,
///     and <see cref="Auth.EntraAuthCodeConfidentialClientFactory" />. Always a type check on the chain, never a
///     message/string match — a string match would be fragile across locales and MSAL versions.
/// </remarks>
public static class EntraCachePersistenceFailure
{
    public static bool IsPersistenceUnavailable(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MsalCachePersistenceException)
            {
                return true;
            }
        }

        return false;
    }
}
