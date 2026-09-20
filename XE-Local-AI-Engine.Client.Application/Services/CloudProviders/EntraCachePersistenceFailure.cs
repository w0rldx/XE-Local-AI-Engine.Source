namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Identity.Client.Extensions.Msal;

/// <summary>
///     Detects an MSAL.NET token-cache persistence failure (DPAPI / Keychain / libsecret unavailable) anywhere in an
///     exception's <see cref="Exception.InnerException" /> chain, not just at the top level.
/// </summary>
/// <remarks>
///     On a Linux box with no <c>org.freedesktop.secrets</c> provider (WSL2 without gnome-keyring/kwallet) this arrives as <c>AuthenticationFailedException</c>
///     wrapping <see cref="MsalCachePersistenceException" /> several levels deep instead of <see cref="Azure.Identity.CredentialUnavailableException" />, so every
///     no-persistence-retry fallback here checks BOTH. Always a type check on the chain, never a message match: a string match is fragile across locales and MSAL versions.
///     The fallbacks, and the unhandled 500 that proved it: docs/wiki/03-local-runtime-and-providers.md "Entra ID sign-in and the token cache".
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
