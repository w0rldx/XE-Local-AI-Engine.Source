namespace XE_Local_AI_Engine.Providers.CodexOAuth;

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// Builds the Codex OAuth inner <see cref="IChatClient"/> over the OpenAI Responses transport,
/// the cloud-factory analogue of <c>AzureFoundryChatClientFactory</c>.
///
/// <para>
/// Owns ONE shared <see cref="SocketsHttpHandler"/> → <see cref="CodexAuthHandler"/> → <see cref="HttpClient"/>
/// chain for the provider's lifetime. The returned <see cref="IChatClient"/> shares this client and does
/// not dispose it. The SDK <c>RetryPolicy</c> is disabled so the only retry layer is the auth handler's
/// refresh-on-401. SDK transport logging stays OFF (plain <see cref="HttpClientPipelineTransport"/> ctor).
/// </para>
/// </summary>
public sealed class CodexOAuthChatClientFactory : ICodexOAuthChatClientFactory, IDisposable
{
    private readonly CodexOptions _options;
    private readonly ICodexTokenStore _tokenStore;
    private readonly SocketsHttpHandler _socketsHandler;
    private readonly CodexAuthHandler _authHandler;
    private readonly HttpClient _httpClient;

    public CodexOAuthChatClientFactory(IOptions<CodexOptions> options,
        ICodexTokenStore tokenStore,
        CodexAuthHandler authHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(authHandler);

        _options = options.Value;
        _tokenStore = tokenStore;
        _authHandler = authHandler;

        // One shared handler chain for the provider lifetime: SocketsHttpHandler -> CodexAuthHandler.
        _socketsHandler = new SocketsHttpHandler();
        _authHandler.InnerHandler = _socketsHandler;
        _httpClient = new HttpClient(_authHandler, disposeHandler: false);
    }

    /// <inheritdoc />
    public AgentModelCapabilities Capabilities => CodexProviderCapabilities.V0;

    /// <inheritdoc />
    public IChatClient Create(string? modelId = null)
    {
        // Mirror AzureFoundryChatClientFactory's credential check: require a session before building.
        var tokens = _tokenStore.LoadAsync().GetAwaiter().GetResult();
        if (tokens is null)
        {
            throw new CodexProviderException(
                CodexProviderErrorKind.AuthRequired,
                "No Codex session is available. Sign in via Codex login first.");
        }

        var resolvedModel = string.IsNullOrWhiteSpace(modelId) ? _options.DefaultModel : modelId;

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = _options.BaseUrl,
            // Wrap the shared HttpClient (handler chain includes CodexAuthHandler). Plain ctor => SDK transport logging OFF.
            Transport = new HttpClientPipelineTransport(_httpClient),
            // Disable the SDK retry layer; the single retry layer is CodexAuthHandler's refresh-on-401.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        // Dummy key only satisfies the SDK ctor; CodexAuthHandler strips/replaces the resulting Authorization.
        var openAiClient = new OpenAIClient(new ApiKeyCredential(CodexChatClientConstruction.DummyApiKey), clientOptions);
        var inner = openAiClient.GetResponsesClient().AsIChatClient(resolvedModel);

        // Enforce store=false on every call, pin the request to the resolved (valid) Codex model id so a leaked
        // local model name can never reach the backend (400 fix), and protect the shared HttpClient from disposal.
        return new CodexStoreDisabledChatClient(inner, resolvedModel);
    }

    public void Dispose()
    {
        // The factory owns the shared transport; tear it down with the provider.
        _httpClient.Dispose();
        _authHandler.Dispose();
        _socketsHandler.Dispose();
    }
}
