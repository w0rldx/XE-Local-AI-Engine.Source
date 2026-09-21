namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Caching.Memory;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
        // Persistence boundary for the identity database. Scoped, and sharing its scope's context with Identity's own
        // UserManager/SignInManager stores: that is what puts an Identity write inside the auth service's transaction.
        builder.Services.AddScoped<INodeIdentityStore, NodeIdentityStore>();
        builder.Services.AddScoped<INodeAuthService, NodeAuthService>();
        builder.Services.AddScoped<INodeTutorialStateService, NodeTutorialStateService>();
        builder.Services.AddSingleton<NodeIdentityInitializationService>();
        builder.Services.AddSingleton<ICloudCredentialStore, CloudCredentialStore>();

        // One place that answers "is this model id a cloud model?" from the stored config, including the best-effort
        // catch — the local-model list/details/select endpoints each used to carry their own copy.
        builder.Services.AddSingleton<ICloudModelResolver, CloudModelResolver>();

        // Node settings: NodeSettingsStore is the canonical inner store (semaphore + 0600 perms) and a single-entry IMemoryCache
        // decorator fronts it as INodeSettingsStore; INodeRuntimeSettings is the read surface, taking stored over seed over default.
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<NodeSettingsStore>();
        builder.Services.AddSingleton<INodeSettingsStore>(static sp =>
            new CachedNodeSettingsStore(sp.GetRequiredService<NodeSettingsStore>(), sp.GetRequiredService<IMemoryCache>()));
        builder.Services.AddSingleton<INodeRuntimeSettings, NodeRuntimeSettings>();
        builder.Services.AddSingleton<INodeSettingsAdministrationService, NodeSettingsAdministrationService>();

        // Encrypted at-rest store for the Entra ID public-client authentication record (device-code / interactive-
        // browser silent-auth resume), read by AzureFoundryChatClientFactory and written by the sign-in coordinator.
        builder.Services.AddSingleton<IEntraTokenCacheStore, EntraTokenCacheStore>();

        // Keeps the authenticated device-code credential alive for the process lifetime: the sign-in coordinator writes it and
        // AzureFoundryChatClientFactory reads it per send, so a send never depends on OS-native persistence (no libsecret on WSL).
        builder.Services.AddSingleton<IEntraLiveCredentialCache, EntraLiveCredentialCache>();

        // Encrypted at-rest store for the authorization-code flow's MSAL home-account-id, read by AzureFoundryChatClientFactory
        // and written by the auth-code coordinator; IEntraTokenCacheStore is its Azure.Identity device-code/browser twin.
        builder.Services.AddSingleton<IEntraAuthCodeAccountStore, EntraAuthCodeAccountStore>();
        builder.Services.AddSingleton<IEntraAuthCodeRedeemer, EntraAuthCodeRedeemer>();
        builder.Services.AddSingleton<IAzureFoundryChatClientFactory, AzureFoundryChatClientFactory>();
        builder.AddCodexOAuthProvider(configuration);

        // Singleton: owns the cross-request pending Entra ID device-code sign-in state the Operator status endpoint polls,
        // mirroring ICodexLoginCoordinator; its success callback invalidates the active-cloud snapshot, so the next send uses it.
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
