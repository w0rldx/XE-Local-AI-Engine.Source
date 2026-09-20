namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

/// <summary>
///     Registers the Codex OAuth cloud provider's auth lifecycle, from options binding to the login coordinator behind
///     the Operator endpoints.
/// </summary>
/// <remarks>
///     The auth service's named token-endpoint <see cref="HttpClient" /> carries NO resilience pipeline: Aspire's
///     service defaults install <c>AddStandardResilienceHandler</c> through <c>ConfigureHttpClientDefaults</c>,
///     retrying every method, and both requests here are single-use grants — the authorization code is consumed by the
///     first exchange and the refresh token ROTATES, so a retry replays a spent token and forces a re-login. Removing
///     the handler never undoes its <c>Timeout.InfiniteTimeSpan</c> mutation, hence the explicit finite backstop.
/// </remarks>
internal static class AddCodexOAuthProviderExtensions
{
    /// <summary>
    ///     Named <see cref="HttpClient" /> for the Codex OAuth token endpoint (code exchange / refresh). Internal rather
    ///     than private so <c>CodexOAuthTokenEndpointResilienceTests</c> names the same client the registration does.
    /// </summary>
    internal const string CodexAuthHttpClientName = "CodexOAuthTokenEndpoint";

    /// <summary>
    ///     Backstop deadline on the token client, restoring the one Aspire's standard pipeline takes away.
    /// </summary>
    /// <remarks>
    ///     Stays above <c>CodexOptions.TokenRequestTimeout</c>, the per-request budget that should be what actually ends
    ///     a slow code exchange or refresh.
    /// </remarks>
    private static readonly TimeSpan TokenEndpointBackstopTimeout = TimeSpan.FromMinutes(2);

    internal static IHostApplicationBuilder AddCodexOAuthProvider(this IHostApplicationBuilder builder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<CodexOptions>()
               .Bind(configuration.GetSection(CodexOptions.SectionName))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        // Encrypted at-rest session store (IDataProtector, separate codex-oauth-tokens.enc). NOT ICloudCredentialStore.
        builder.Services.AddSingleton<ICodexTokenStore, CodexTokenStore>();

        // A dedicated NAMED HttpClient for the OAuth token endpoint, built through IHttpClientFactory only when the auth service runs.
        // It must NOT carry CodexAuthHandler (chat transport only); the handler strip and the finite timeout are both required — see the type remarks.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; used deliberately to drop the
        // Aspire-installed standard pipeline, whose blanket retries replay a consumed,
        // rotated refresh token.
        builder.Services.AddHttpClient(CodexAuthHttpClientName, static client => client.Timeout = TokenEndpointBackstopTimeout)
               .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<ICodexAuthService>(serviceProvider =>
        {
            var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(CodexAuthHttpClientName);
            return new CodexAuthService(serviceProvider.GetRequiredService<IOptions<CodexOptions>>(),
                httpClient,
                serviceProvider.GetRequiredService<ICodexTokenStore>(),
                serviceProvider.GetRequiredService<ILogger<CodexAuthService>>(),
                serviceProvider.GetRequiredService<TimeProvider>());
        });

        // Decorates the Codex chat transport: injects the bearer + account headers and single-flights refresh on 401.
        // Transient so the chat-client factory (model-runtime module) can pull a fresh handler per HttpClient.
        builder.Services.TryAddTransient<CodexAuthHandler>();

        // Lazy<ICodexAuthService> so the coordinator (a singleton instantiated when the Operator login endpoint is
        // first constructed) does not eagerly build the auth service's HttpClient at host startup.
        builder.Services.AddSingleton(serviceProvider =>
            new Lazy<ICodexAuthService>(serviceProvider.GetRequiredService<ICodexAuthService>));

        // Singleton: owns the cross-request pending-login state the Operator status endpoint polls; its success callback invalidates
        // the active-cloud snapshot, so a sign-in lands on the next send. The selector resolves at invoke time, off the host startup path.
        builder.Services.AddSingleton<ICodexLoginCoordinator>(serviceProvider => new CodexLoginCoordinator(serviceProvider.GetRequiredService<Lazy<ICodexAuthService>>(),
            serviceProvider.GetRequiredService<ILogger<CodexLoginCoordinator>>(),
            () => serviceProvider.GetRequiredService<IActiveCloudChatClientFactory>().InvalidateSelectionCache()));

        // Cloud chat-client factory, parallel to IAzureFoundryChatClientFactory. Singleton because it owns a
        // shared HttpClient + CodexAuthHandler transport (IDisposable); the per-call Create() is cheap.
        builder.Services.AddSingleton<ICodexOAuthChatClientFactory, CodexOAuthChatClientFactory>();

        // Lazy so the active-cloud selector, which FastEndpoints builds eagerly when it instantiates the endpoints at startup,
        // does not construct the Codex chat factory's HttpClient transport until a Codex client is really built on a send.
        builder.Services.AddSingleton(serviceProvider =>
            new Lazy<ICodexOAuthChatClientFactory>(serviceProvider.GetRequiredService<ICodexOAuthChatClientFactory>));

        // Re-resolves the active cloud client (Codex/Azure) per send so a sign-in/sign-out takes effect immediately.
        builder.Services.AddSingleton<IActiveCloudChatClientFactory, ActiveCloudChatClientFactory>();

        // Session lifecycle behind the cloud/codex/* Operator endpoints. Singleton, matching every dependency it wraps.
        builder.Services.AddSingleton<CodexSessionService>();

        // Attributes a terminalized turn's tokens to the fine-grained provider (local/ollama/codex/azure/unknown) for the
        // usage ledger; composed from the cloud selector above + the local model→provider resolver.
        builder.Services.AddSingleton<IUsageProviderResolver, UsageProviderResolver>();

        return builder;
    }
}
