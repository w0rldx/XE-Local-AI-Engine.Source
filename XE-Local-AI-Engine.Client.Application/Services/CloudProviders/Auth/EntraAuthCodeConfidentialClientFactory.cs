namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;

using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Builds the MSAL confidential-client app shared by the authorization-code redeemer (initial redemption) and
///     the chat-client factory's silent-rebuild path (subsequent sends / process restarts) — both must point at the
///     SAME persistent-cache file so a token acquired during sign-in is found by the very next send.
/// </summary>
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
    ///     token cache. On a platform where that persistence is unavailable (e.g. no libsecret on a headless Linux
    ///     box), MSAL keeps using its own default in-process cache instead — logged once, never thrown, and never
    ///     falls back to writing the cache unencrypted on disk (mirrors <c>EntraPersistenceFallbackCredential</c>'s
    ///     philosophy for the device-code / interactive-browser credentials).
    /// </summary>
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

            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);

            // CreateAsync can succeed even when the backend is silently broken (e.g. dbus present but
            // org.freedesktop.secrets not provided by any service, live-confirmed on WSL2) — VerifyPersistence()
            // round-trips a real test blob through the storage backend so that case is caught HERE, not later when
            // an actual sign-in's token save fails.
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
