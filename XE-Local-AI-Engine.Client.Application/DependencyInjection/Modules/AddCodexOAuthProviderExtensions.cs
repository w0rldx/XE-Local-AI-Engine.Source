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
///     Registers the Codex OAuth cloud provider's auth lifecycle: options binding, the encrypted
///     token store, the auth service (with its own named token-endpoint <see cref="HttpClient" />), the
///     <see cref="CodexAuthHandler" /> that decorates the chat transport, the cloud chat-client factory + active-cloud
///     selector, and the singleton login coordinator that owns the pending-login state behind the Operator endpoints.
/// </summary>
internal static class AddCodexOAuthProviderExtensions
{
    /// <summary>
    ///     Named <see cref="HttpClient" /> for the Codex OAuth token endpoint (code exchange / refresh). Internal rather
    ///     than private so <c>CodexOAuthTokenEndpointResilienceTests</c> names the same client the registration does.
    /// </summary>
    internal const string CodexAuthHttpClientName = "CodexOAuthTokenEndpoint";

    /// <summary>
    ///     Backstop deadline on the token client, restoring one that Aspire's standard pipeline takes away (see the
    ///     registration below). Must stay above <c>CodexOptions.TokenRequestTimeout</c>, the per-request budget that
    ///     should be what actually ends a slow code exchange or refresh.
    /// </summary>
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

        // The auth service owns a dedicated HttpClient for the OAuth token endpoint (code exchange / refresh).
        // This client must NOT carry the CodexAuthHandler — that handler decorates the chat transport, not auth.
        // A NAMED client (resolved via IHttpClientFactory at first use) is used rather than the typed-client
        // overload so the HttpClient is created only when the auth service actually runs (see the Lazy below).
        //
        // NO resilience pipeline on it. Under Aspire, AddServiceDefaults installs a standard handler on every client
        // through ConfigureHttpClientDefaults, and that pipeline retries every method by default. Both requests this
        // client sends are single-use OAuth grants: the authorization code is consumed by the first exchange, and the
        // refresh token ROTATES — so a transport-level retry after a slow-but-successful refresh replays a token the
        // server has already spent, the endpoint answers invalid_grant, and the operator is forced back through a full
        // re-login. RemoveAllResilienceHandlers strips it, and is a no-op outside Aspire.
        //
        // The explicit timeout is NOT redundant and must not be dropped. AddStandardResilienceHandler sets
        // HttpClient.Timeout to Timeout.InfiniteTimeSpan — its pipeline owns the deadline instead — and
        // RemoveAllResilienceHandlers removes the HANDLER, never that mutation. A client that only strips therefore
        // ends up under Aspire with no pipeline AND no client deadline, and a sign-in would hang with nothing left to
        // end it. This stays FINITE deliberately, above CodexAuthService's own TokenRequestTimeout — the linked-token
        // budget that brackets each token request and is what should fire first.
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

        // Singleton: owns the cross-request pending-login state the Operator status endpoint polls. The
        // onLoginSucceeded callback invalidates the active-cloud selection snapshot when a login completes, so a
        // sign-in takes effect on the very next send (not after the snapshot TTL). It resolves the selector at
        // invoke-time (login success, background) — never at coordinator construction — to keep host startup from
        // eagerly building the Codex chat-client transport chain.
        builder.Services.AddSingleton<ICodexLoginCoordinator>(serviceProvider => new CodexLoginCoordinator(serviceProvider.GetRequiredService<Lazy<ICodexAuthService>>(),
            serviceProvider.GetRequiredService<ILogger<CodexLoginCoordinator>>(),
            () => serviceProvider.GetRequiredService<IActiveCloudChatClientFactory>().InvalidateSelectionCache()));

        // Cloud chat-client factory, parallel to IAzureFoundryChatClientFactory. Singleton because it owns a
        // shared HttpClient + CodexAuthHandler transport (IDisposable); the per-call Create() is cheap.
        builder.Services.AddSingleton<ICodexOAuthChatClientFactory, CodexOAuthChatClientFactory>();

        // Lazy<ICodexOAuthChatClientFactory> so the active-cloud selector (built eagerly when FastEndpoints
        // instantiates the endpoints at startup) does not construct the Codex chat factory's HttpClient transport
        // until a Codex client is actually built (a real send).
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
