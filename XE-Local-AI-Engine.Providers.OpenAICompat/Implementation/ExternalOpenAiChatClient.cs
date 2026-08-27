namespace XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;

using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     The <see cref="IChatClient" /> for ONE registered external model, resolved against the registry on first use.
/// </summary>
/// <remarks>
///     <para>
///         Construction is deferred for the same reason the llama-server client defers it:
///         <see cref="Abstractions.ILocalModelProvider.CreateChatClient" /> is synchronous while resolving the
///         connection (and decrypting its key) is asynchronous. Paying that cost on the first send is a normal
///         first-token delay; blocking the sync factory on it is not.
///     </para>
///     <para>
///         The registration is re-read on EVERY send and the built adapter is rebuilt whenever the endpoint identity
///         changes, so an operator who edits a connection's base URL or timeout does not keep talking to the old
///         address. The API key is read only when the adapter is (re)built — a key edit is covered by the save path's
///         chat-client cache invalidation, which drops this instance outright.
///     </para>
///     <para>
///         An unresolvable id is TERMINAL, never a fallback: without a resolved registration there is no operator
///         locality declaration to honour, and a prompt must not be sent to a guessed endpoint.
///     </para>
/// </remarks>
internal sealed class ExternalOpenAiChatClient : IChatClient
{
    /// <summary>
    ///     Outer per-call network deadline when the connection declares none. Generous: a self-hosted 27B model on CPU
    ///     legitimately takes minutes for a long answer, and the turn-level invocation timeout owns the real bound.
    /// </summary>
    internal static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Bounds ONLY TCP connection establishment, so an endpoint that is down (or silently drops the SYN) fails in
    ///     about a second instead of waiting out the OS connect timeout. It never shortens a genuine generation.
    /// </summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _initGate = new(initialCount: 1, maxCount: 1);
    private readonly Func<HttpMessageHandler>? _transportHandlerFactory;
    private readonly string _modelId;
    private readonly IExternalProviderRegistry _registry;

    private ResolvedEndpoint? _resolved;

    /// <param name="registry">The read-only registry the connection and key are resolved from.</param>
    /// <param name="modelId">The namespaced <c>ext:{connectionId}/{wireId}</c> id this client serves.</param>
    /// <param name="transportHandlerFactory">
    ///     Test seam: supplies the INNERMOST handler so a request can be driven through the real assembled pipeline —
    ///     endpoint guard included — without live network I/O. <see langword="null" /> in production.
    /// </param>
    public ExternalOpenAiChatClient(IExternalProviderRegistry registry, string modelId, Func<HttpMessageHandler>? transportHandlerFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _modelId = modelId;
        _transportHandlerFactory = transportHandlerFactory;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (client, model) = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
        return await client.GetResponseAsync(messages, ExternalReasoningEffort.Apply(options, model), cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var (client, model) = await EnsureInnerAsync(cancellationToken).ConfigureAwait(false);
        var patched = ExternalReasoningEffort.Apply(options, model);
        await foreach (var update in client.GetStreamingResponseAsync(messages, patched, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        // Defer to the inner adapter only once it has been constructed; before first use there is nothing to forward.
        return Volatile.Read(ref _resolved)?.Client.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _resolved, value: null)?.Dispose();
        _initGate.Dispose();
    }

    private async Task<(IChatClient Client, ExternalProviderModelDescriptor Model)> EnsureInnerAsync(CancellationToken ct)
    {
        var registration = await _registry.TryResolveAsync(_modelId, ct).ConfigureAwait(false)
                           ?? throw new ExternalProviderModelUnavailableException();

        var identity = EndpointIdentity.From(registration);
        var current = Volatile.Read(ref _resolved);
        if (current is not null && current.Identity == identity)
        {
            return (current.Client, registration.Model);
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = Volatile.Read(ref _resolved);
            if (current is not null && current.Identity == identity)
            {
                return (current.Client, registration.Model);
            }

            var apiKey = await _registry.GetApiKeyAsync(registration.Connection.Id, ct).ConfigureAwait(false);
            // The built stack is transferred into _resolved, which owns it until it is replaced (the previous one is
            // disposed just below) or this client is disposed. CA2000 cannot follow that ownership transfer.
#pragma warning disable CA2000
            var built = Build(registration, apiKey, identity);
#pragma warning restore CA2000
            Volatile.Write(ref _resolved, built);
            current?.Dispose();
            return (built.Client, registration.Model);
        }
        finally
        {
            _ = _initGate.Release();
        }
    }

    // Assembles the full per-connection stack: hardened transport -> endpoint guard -> OpenAI chat-completions adapter
    // -> reasoning rewriting. Every disposable created here transfers into the returned ResolvedEndpoint, which owns
    // them for as long as it is the current one; CA2000 cannot follow that ownership transfer.
#pragma warning disable CA2000
    private ResolvedEndpoint Build(ExternalProviderModelRegistration registration, string? apiKey, EndpointIdentity identity)
    {
        var baseAddress = OpenAICompatibleBaseAddress.Normalize(registration.Connection.BaseUrl);
        var inner = _transportHandlerFactory?.Invoke()
                    ?? new SocketsHttpHandler
                    {
                        ConnectTimeout = ConnectTimeout,
                        // Never follow a redirect: the operator reviewed ONE base address when they declared this
                        // connection's locality, and a 3xx to another host would move the prompt somewhere they did not.
                        AllowAutoRedirect = false
                    };

        var guarded = new ExternalEndpointGuardHandler(baseAddress, inner);
        var httpClient = new HttpClient(guarded, disposeHandler: true)
        {
            // The SDK's pinned NetworkTimeout owns the deadline; HttpClient's own 100 s default would otherwise cut a
            // legitimately long generation short well before it.
            Timeout = Timeout.InfiniteTimeSpan
        };

        var adapter = OpenAICompatibleClientFactory.CreateChatClient(baseAddress,
            registration.Model.WireId,
            apiKey,
            registration.Connection.Timeout ?? DefaultNetworkTimeout,
            new HttpClientPipelineTransport(httpClient));

        return new ResolvedEndpoint(identity, new ExternalReasoningRewritingChatClient(adapter), httpClient);
    }
#pragma warning restore CA2000

    /// <summary>
    ///     The endpoint facts a built adapter is bound to. When any of them changes the adapter must be rebuilt, so the
    ///     comparison is what makes an operator's connection edit take effect on the next send.
    /// </summary>
    private readonly record struct EndpointIdentity(string BaseUrl, string WireId, TimeSpan Timeout)
    {
        public static EndpointIdentity From(ExternalProviderModelRegistration registration)
        {
            return new EndpointIdentity(registration.Connection.BaseUrl.AbsoluteUri,
                registration.Model.WireId,
                registration.Connection.Timeout ?? DefaultNetworkTimeout);
        }
    }

    private sealed class ResolvedEndpoint(EndpointIdentity identity, IChatClient client, HttpClient httpClient) : IDisposable
    {
        public EndpointIdentity Identity { get; } = identity;

        public IChatClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            httpClient.Dispose();
        }
    }
}
