namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Persistence boundary for the single Entra ID authorization-code sign-in's MSAL home-account-id (silent-auth
///     resume). Parallel to <see cref="IEntraTokenCacheStore" />, which is shaped around Azure.Identity's
///     <see cref="Azure.Identity.AuthenticationRecord" /> — MSAL's <c>IAccount</c> is looked up from
///     <c>IClientApplicationBase.GetAccountsAsync()</c> by this id, never persisted itself. Carries no token value.
/// </summary>
public interface IEntraAuthCodeAccountStore
{
    /// <summary>Loads the stored home account id, or <see langword="null" /> if none / undecryptable.</summary>
    Task<string?> LoadHomeAccountIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Encrypts and persists the home account id with user-only file permissions.</summary>
    Task SaveHomeAccountIdAsync(string homeAccountId, CancellationToken cancellationToken = default);

    /// <summary>Removes any stored home account id (sign-out).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
