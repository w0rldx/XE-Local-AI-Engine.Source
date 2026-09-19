namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Caching.Memory;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Tutorial;
using XE_Local_AI_Engine.Client.Services.Tutorial.Implementation;

internal static class AddNodeAuthExtensions
{
    public static IHostApplicationBuilder AddNodeAuth(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddSingleton<ITokenStore, TokenStore>();
        builder.Services.AddSingleton<INodeOperatorSecretProvider, NodeOperatorSecretProvider>();
        builder.Services.AddSingleton<INodeJwtKeyProvider, NodeJwtKeyProvider>();
        builder.Services.AddSingleton<INodeTokenService, NodeTokenService>();
        builder.Services.AddScoped<INodeAuthService, NodeAuthService>();
        builder.Services.AddScoped<INodeTutorialStateService, NodeTutorialStateService>();
        builder.Services.AddSingleton<NodeIdentityInitializationService>();
        builder.Services.AddSingleton<ICloudCredentialStore, CloudCredentialStore>();

        // One place that answers "is this model id a cloud model?" from the stored config, including the best-effort
        // catch — the local-model list/details/select endpoints each used to carry their own copy.
        builder.Services.AddSingleton<ICloudModelResolver, CloudModelResolver>();

        // Node settings: the file store stays the canonical inner store (semaphore + 0600 perms); a single-entry
        // IMemoryCache decorator fronts it as INodeSettingsStore, and INodeRuntimeSettings is the read surface migrated
        // consumers use (precedence stored > appsettings seed > hardcoded default).
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<NodeSettingsStore>();
        builder.Services.AddSingleton<INodeSettingsStore>(static sp =>
            new CachedNodeSettingsStore(sp.GetRequiredService<NodeSettingsStore>(), sp.GetRequiredService<IMemoryCache>()));
        builder.Services.AddSingleton<INodeRuntimeSettings, NodeRuntimeSettings>();
        builder.Services.AddSingleton<INodeSettingsAdministrationService, NodeSettingsAdministrationService>();

        // Encrypted at-rest store for the Entra ID public-client authentication record (device-code / interactive-
        // browser silent-auth resume), read by AzureFoundryChatClientFactory and written by the sign-in coordinator.
        builder.Services.AddSingleton<IEntraTokenCacheStore, EntraTokenCacheStore>();

        // Keeps the live, already-authenticated device-code credential alive for the process lifetime — the sign-in
        // coordinator writes it on success, AzureFoundryChatClientFactory reads it on every send, so a chat send
        // never depends solely on OS-native encrypted persistence (which may be unavailable — e.g. no libsecret on
        // Linux/WSL, forcing an in-memory fallback).
        builder.Services.AddSingleton<IEntraLiveCredentialCache, EntraLiveCredentialCache>();

        // Encrypted at-rest store for the authorization-code flow's MSAL home-account-id, read by
        // AzureFoundryChatClientFactory and written by the auth-code sign-in coordinator (parallel to
        // IEntraTokenCacheStore, which is shaped around Azure.Identity's device-code/browser AuthenticationRecord).
        builder.Services.AddSingleton<IEntraAuthCodeAccountStore, EntraAuthCodeAccountStore>();
        builder.Services.AddSingleton<IEntraAuthCodeRedeemer, EntraAuthCodeRedeemer>();
        builder.Services.AddSingleton<IAzureFoundryChatClientFactory, AzureFoundryChatClientFactory>();
        builder.AddCodexOAuthProvider(configuration);

        // Singleton: owns the cross-request pending Entra ID device-code sign-in state the Operator status endpoint
        // polls, mirroring ICodexLoginCoordinator. The onSignInSucceeded callback invalidates the active-cloud
        // selection snapshot so a sign-in takes effect on the very next send.
        builder.Services.AddSingleton<IEntraDeviceCodeSignInCoordinator>(serviceProvider => new EntraDeviceCodeSignInCoordinator(serviceProvider.GetRequiredService<ICloudCredentialStore>(),
            serviceProvider.GetRequiredService<IEntraTokenCacheStore>(),
            serviceProvider.GetRequiredService<IEntraLiveCredentialCache>(),
            serviceProvider.GetRequiredService<ILogger<EntraDeviceCodeSignInCoordinator>>(),
            () => serviceProvider.GetRequiredService<IActiveCloudChatClientFactory>().InvalidateSelectionCache()));

        // Singleton: owns the cross-request pending Entra ID authorization-code sign-in state the Operator status
        // endpoint polls, mirroring the device-code coordinator above.
        builder.Services.AddSingleton<IEntraAuthCodeSignInCoordinator>(serviceProvider => new EntraAuthCodeSignInCoordinator(serviceProvider.GetRequiredService<ICloudCredentialStore>(),
            serviceProvider.GetRequiredService<IEntraAuthCodeAccountStore>(),
            serviceProvider.GetRequiredService<IEntraLiveCredentialCache>(),
            serviceProvider.GetRequiredService<IEntraAuthCodeRedeemer>(),
            serviceProvider.GetRequiredService<ILogger<EntraAuthCodeSignInCoordinator>>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            () => serviceProvider.GetRequiredService<IActiveCloudChatClientFactory>().InvalidateSelectionCache()));

        return builder;
    }
}
