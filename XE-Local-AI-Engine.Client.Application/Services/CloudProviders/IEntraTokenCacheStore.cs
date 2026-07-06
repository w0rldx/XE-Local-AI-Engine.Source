namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Azure.Identity;

/// <summary>
///     Persistence boundary for the single Entra ID public-client <see cref="AuthenticationRecord" /> (device-code /
///     interactive-browser silent-auth resume). Distinct from the OS-native MSAL token cache Azure.Identity manages
///     itself via <see cref="TokenCachePersistenceOptions" /> — this store persists only the account descriptor
///     needed to attempt silent auth, never a token value.
/// </summary>
public interface IEntraTokenCacheStore
{
    /// <summary>Loads the stored record, or <see langword="null" /> if none / undecryptable.</summary>
    Task<AuthenticationRecord?> LoadRecordAsync(CancellationToken cancellationToken = default);

    /// <summary>Encrypts and persists the record with user-only file permissions.</summary>
    Task SaveRecordAsync(AuthenticationRecord record, CancellationToken cancellationToken = default);

    /// <summary>Removes any stored record (sign-out).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
