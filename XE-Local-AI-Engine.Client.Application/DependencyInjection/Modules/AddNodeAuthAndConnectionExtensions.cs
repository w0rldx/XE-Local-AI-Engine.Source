namespace XE_Local_AI_Engine.Client;

using Microsoft.Extensions.Caching.Memory;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Tutorial;
using XE_Local_AI_Engine.Client.Services.Tutorial.Implementation;

internal static class AddNodeAuthAndConnectionExtensions
{
    public static IHostApplicationBuilder AddNodeAuthAndConnection(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // CentralPlatform:BaseUrl is optional for local-only deployments (LocalTester release profile).
        // When absent the node runs fully local: no platform HTTP client, no cloud-provider registration,
        // and the Cloud Settings UI surface is hidden via the cloudSettings capability flag (NodeCapabilities.ts).
        var centralPlatformBaseUrl = configuration.GetValue<string>("CentralPlatform:BaseUrl");
        var hasCentralPlatform = !string.IsNullOrWhiteSpace(centralPlatformBaseUrl);

        if (hasCentralPlatform)
        {
            if (!centralPlatformBaseUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (builder.Environment.IsDevelopment())
                {
                    Console.Error.WriteLine("WARNING: CentralPlatform:BaseUrl is not HTTPS. Tokens may be transmitted in plaintext.");
                }
                else
                {
                    throw new InvalidOperationException("CentralPlatform:BaseUrl must use HTTPS in non-development environments.");
                }
            }

            builder.Services.AddHttpClient("CentralPlatformApi", client =>
            {
                client.BaseAddress = new Uri(centralPlatformBaseUrl, UriKind.Absolute);
            }).AddStandardResilienceHandler();
        }

        builder.Services.AddSingleton<ITokenStore, TokenStore>();
        builder.Services.AddSingleton<INodeOperatorSecretProvider, NodeOperatorSecretProvider>();
        builder.Services.AddSingleton<INodeJwtKeyProvider, NodeJwtKeyProvider>();
        builder.Services.AddSingleton<INodeTokenService, NodeTokenService>();
        builder.Services.AddScoped<INodeAuthService, NodeAuthService>();
        builder.Services.AddScoped<INodeTutorialStateService, NodeTutorialStateService>();
        builder.Services.AddSingleton<NodeIdentityInitializationService>();
        builder.Services.AddSingleton<ICloudCredentialStore, CloudCredentialStore>();

        // Node settings: the file store stays the canonical inner store (semaphore + 0600 perms); a single-entry
        // IMemoryCache decorator fronts it as INodeSettingsStore, and INodeRuntimeSettings is the read surface migrated
        // consumers use (precedence stored > appsettings seed > hardcoded default).
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<NodeSettingsStore>();
        builder.Services.AddSingleton<INodeSettingsStore>(static sp =>
            new CachedNodeSettingsStore(sp.GetRequiredService<NodeSettingsStore>(), sp.GetRequiredService<IMemoryCache>()));
        builder.Services.AddSingleton<INodeRuntimeSettings, NodeRuntimeSettings>();
        builder.Services.AddSingleton<IAzureFoundryChatClientFactory, AzureFoundryChatClientFactory>();
        builder.AddCodexOAuthProvider(configuration);
        builder.Services.AddSingleton<INodeKeyRegistry, NodeKeyRegistry>();
        builder.Services.AddSingleton<IPairingService, PairingService>();
        builder.Services.AddSingleton<IWorkerTokenRefreshService, WorkerTokenRefreshService>();
        builder.Services.AddSingleton<INodeBindingService, NodeBindingService>();
        builder.Services.AddSingleton<ConnectionState>();
        builder.Services.AddSingleton<IConnectionControlService, ConnectionControlService>();

        return builder;
    }
}
