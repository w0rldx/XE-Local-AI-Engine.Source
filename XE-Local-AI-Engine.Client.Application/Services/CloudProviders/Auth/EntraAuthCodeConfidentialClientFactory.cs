namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Builds the MSAL confidential-client app shared by the authorization-code redeemer and by the chat-client
///     factory's silent-rebuild path.
/// </summary>
/// <remarks>
///     Both callers must point at the SAME persistent-cache file, or a token acquired during sign-in is not found by
///     the very next send.
/// </remarks>
internal static class EntraAuthCodeConfidentialClientFactory
{
    private const string CacheFileName = "entra-authcode-msal.cache";
    private const string KeyringSchemaName = "com.xe-local-ai-engine.msal.authcode";
    private const string KeyChainServiceName = "com.xe-local-ai-engine.msal.authcode";
    private const string KeyChainAccountName = "MSALCache";

    public static IConfidentialClientApplication Build(string tenantId, string clientId, string clientSecret, string redirectUri)
    {
        return ConfidentialClientApplicationBuilder.Create(clientId)
                                                   .WithClientSecret(clientSecret)
                                                   .WithTenantId(tenantId)
                                                   .WithRedirectUri(redirectUri)
                                                   .Build();
    }

    /// <summary>
    ///     Best-effort: registers OS-native encrypted persistence (DPAPI / Keychain / libsecret) on the app's user
    ///     token cache.
    /// </summary>
    /// <remarks>
    ///     Where that persistence is unavailable (no libsecret on a headless Linux box) MSAL keeps its own default
    ///     in-process cache — logged once, never thrown, and never falling back to an unencrypted cache on disk.
    ///     Mirrors <c>EntraPersistenceFallbackCredential</c>'s philosophy for the device-code / interactive-browser
    ///     credentials.
    /// </remarks>
    public static async Task TryRegisterPersistentCacheAsync(IConfidentialClientApplication app, INodeDataDirectory dataDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var storageProperties = new StorageCreationPropertiesBuilder(CacheFileName, dataDirectory.Root)
                                    .WithLinuxKeyring(KeyringSchemaName,
                                        "default",
                                        "MSAL token cache for XE-Local-AI-Engine",
                                        new KeyValuePair<string, string>("Version", "1"),
                                        new KeyValuePair<string, string>("ProductGroup", "XE-Local-AI-Engine"))
                                    .WithMacKeyChain(KeyChainServiceName, KeyChainAccountName)
                                    .Build();

            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);

            // CreateAsync succeeds even when the backend is silently broken (dbus present, org.freedesktop.secrets provided by
            // nobody — live-confirmed on WSL2): VerifyPersistence round-trips a real blob, so that fails HERE, not at a token save.
            cacheHelper.VerifyPersistence();
            cacheHelper.RegisterCache(app.UserTokenCache);
        }
        catch (Exception exception) when (EntraCachePersistenceFailure.IsPersistenceUnavailable(exception) || exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Encrypted MSAL token-cache persistence is unavailable on this platform; the Entra ID authorization-code sign-in will use an in-memory (non-persisted) token cache and require re-sign-in after restart.");
        }
    }
}
