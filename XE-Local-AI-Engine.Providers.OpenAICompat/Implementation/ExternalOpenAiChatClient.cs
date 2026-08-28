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
///         The binding is re-read on EVERY send and the built adapter is rebuilt whenever the endpoint identity
///         changes, so an operator who edits a connection's base URL, key or timeout does not keep talking to the old
///         one. It is read as ONE atomic value — endpoint, declared trust, generation and credential together — because
///         reading the address and the key separately lets a concurrent edit present a new key at an old address.
///     </para>
///     <para>
///         When the send belongs to a PINNED invocation (the normal chat/agent turn), the freshly read binding is
///         checked against the pin the turn's tools were authorized against, and a send whose locality, origin or
///         generation no longer matches is refused. Tool authorization happens once per turn while a tool loop sends
///         many times; without this check an operator edit landing mid-loop would redirect the later sends — carrying
///         the already-authorized local tools and their results — to an endpoint that never earned them.
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

    /// <summary>Sentinel for "this client has not resolved a binding yet", distinct from every real locality value.</summary>
    private const int LocalityUnseen = -1;

    private readonly SemaphoreSlim _initGate = new(initialCount: 1, maxCount: 1);
    private readonly Func<HttpMessageHandler>? _transportHandlerFactory;
    private readonly string _modelId;
    private readonly IExternalProviderRegistry _registry;

    // The locality this client FIRST resolved, as an int so it can be published with one interlocked write.
    private int _firstSeenLocality = LocalityUnseen;

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
        var transportBinding = await _registry.TryResolveTransportBindingAsync(_modelId, ct).ConfigureAwait(false)
                               ?? throw new ExternalProviderModelUnavailableException();

        var binding = transportBinding.Binding;
        VerifyStillAuthorized(binding);

        var registration = binding.Registration;
        var identity = EndpointIdentity.From(registration, transportBinding.ApiKey);
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

            // The built stack is transferred into _resolved, which owns it until it is replaced (the previous one is
            // disposed just below) or this client is disposed. CA2000 cannot follow that ownership transfer.
#pragma warning disable CA2000
            var built = Build(registration, transportBinding.ApiKey, identity);
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

    /// <summary>
    ///     Refuses a send whose binding no longer matches what its invocation was authorized against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         PINNED sends (a chat or agent turn) compare against the pin the tool offer was computed from: locality,
    ///         origin and registry generation. That is the mid-invocation swap this closes — editing the connection
    ///         Local→Cloud, or moving it to another host, between two rounds of one tool loop.
    ///     </para>
    ///     <para>
    ///         UNPINNED sends (a background summarization, a health-adjacent probe — contexts with no tool offer to
    ///         invalidate) resolve live, but may never become MORE privileged than the declaration this client was
    ///         first built against: a connection that was Cloud when this instance resolved it and is Local now has
    ///         escalated underneath a live client, and re-using it is refused rather than silently honoured.
    ///     </para>
    /// </remarks>
    private void VerifyStillAuthorized(ExternalProviderBinding binding)
    {
        if (ExternalProviderBindingPinScope.Find(_modelId) is { } pin)
        {
            if (!pin.Matches(binding))
            {
                throw new ExternalProviderBindingChangedException();
            }

            return;
        }

        var firstSeen = Interlocked.CompareExchange(ref _firstSeenLocality, (int)binding.Locality, LocalityUnseen);
        if (firstSeen != LocalityUnseen
            && firstSeen != (int)ExternalProviderLocality.Local
            && binding.Locality == ExternalProviderLocality.Local)
        {
            throw new ExternalProviderBindingChangedException();
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
    /// <remarks>
    ///     The CREDENTIAL is part of the identity. It was not, and that was a hole: rotating or clearing a key changes
    ///     neither the address nor the timeout, so the cached adapter kept presenting the previous key until something
    ///     else happened to evict it — which is the failure an operator experiences as "I fixed the key and it still
    ///     fails", or worse, as a revoked key that keeps working.
    /// </remarks>
    private readonly record struct EndpointIdentity(string BaseUrl, string WireId, TimeSpan Timeout, string? ApiKey)
    {
        public static EndpointIdentity From(ExternalProviderModelRegistration registration, string? apiKey)
        {
            return new EndpointIdentity(registration.Connection.BaseUrl.AbsoluteUri,
                registration.Model.WireId,
                registration.Connection.Timeout ?? DefaultNetworkTimeout,
                apiKey);
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
