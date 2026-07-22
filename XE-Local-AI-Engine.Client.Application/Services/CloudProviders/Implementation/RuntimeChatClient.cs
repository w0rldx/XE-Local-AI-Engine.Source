namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>
///     The node's registered <see cref="IChatClient" />: a stable wrapper that re-selects cloud-vs-local on
///     <b>every</b> call. Singleton consumers (the agent factories) capture this wrapper once,
///     but each send re-evaluates the active provider via <see cref="IActiveCloudChatClientFactory" />, so signing
///     in or out at runtime takes effect on the next send without restarting the node.
///     <para>
///         The local model client is stable, so it is resolved once and reused. The active cloud client is resolved per
///         call but <see cref="IActiveCloudChatClientFactory" /> caches it on a selection fingerprint, so it is rebuilt
///         only when the selection changes (sign-in / sign-out / refresh) — not on every send. When a cloud provider is
///         selected but unusable (e.g. no Codex session), the cloud factory throws a typed re-auth error, which propagates
///         to the caller as a re-authenticate prompt rather than silently routing local.
///     </para>
/// </summary>
public sealed class RuntimeChatClient : IChatClient
{
    // The active client returned per call is either the cached local client (owned by this wrapper, disposed in
    // Dispose) or the active cloud client, which is owned and lifecycle-managed by IActiveCloudChatClientFactory
    // (it caches the cloud client and does NOT dispose swapped-out wrappers — concurrency-safety, so an in-flight
    // request is never torn down). Disposing the resolved client at this boundary would be incorrect for both —
    // the local client is reused across calls, and the cloud client is owned by the cloud factory — so it is never
    // disposed here.
    private const string ActiveClientOwnershipNote =
        "The resolved client is either the cached local client (disposed in Dispose) or the active cloud client "
        + "owned and lifecycle-managed by IActiveCloudChatClientFactory; disposing it here is incorrect.";

    private readonly IActiveCloudChatClientFactory _activeCloudFactory;
    private readonly ICloudEgressAuthorizer _cloudEgressAuthorizer;
    private readonly Lazy<IChatClient> _localClient;

    public RuntimeChatClient(IActiveCloudChatClientFactory activeCloudFactory,
        Func<IChatClient> localClientFactory,
        ICloudEgressAuthorizer cloudEgressAuthorizer)
    {
        ArgumentNullException.ThrowIfNull(activeCloudFactory);
        ArgumentNullException.ThrowIfNull(localClientFactory);
        ArgumentNullException.ThrowIfNull(cloudEgressAuthorizer);

        _activeCloudFactory = activeCloudFactory;
        _cloudEgressAuthorizer = cloudEgressAuthorizer;
        _localClient = new Lazy<IChatClient>(localClientFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = ActiveClientOwnershipNote)]
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveActiveClient(options).GetResponseAsync(messages, options, cancellationToken);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = ActiveClientOwnershipNote)]
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveActiveClient(options).GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = ActiveClientOwnershipNote)]
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType.IsInstanceOfType(this) && serviceKey is null)
        {
            return this;
        }

        // Delegate metadata/service lookups to the active client so callers see the real provider's services. There
        // is no per-request model id available at this boundary, so this resolves the node-default provider.
        return ResolveActiveClient(options: null).GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        // Only the local client is owned here; cloud clients are owned by their (singleton) factories, which
        // protect their shared transport from disposal. Dispose the local client only if it was created.
        if (_localClient.IsValueCreated)
        {
            _localClient.Value.Dispose();
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = ActiveClientOwnershipNote)]
    private IChatClient ResolveActiveClient(ChatOptions? options)
    {
        var requestedModelId = options?.ModelId;
        if (!_activeCloudFactory.TryCreateActiveCloudChatClient(requestedModelId, out var cloudClient) || cloudClient is null)
        {
            return _localClient.Value;
        }

        AuthorizeDevelopmentCloudRequest(options, requestedModelId);
        return cloudClient;
    }

    private void AuthorizeDevelopmentCloudRequest(ChatOptions? options, string? requestedModelId)
    {
        if (!DevelopmentCloudAuthorizationMetadata.IsDevelopmentMarked(options))
        {
            return;
        }

        var providerName = _activeCloudFactory.ResolveActiveCloudProviderName(requestedModelId)
                           ?? throw new CloudEgressAuthorizationException("The selected cloud provider could not be identified for Development authorization.");
        if (DevelopmentCloudAuthorizationMetadata.TryCreateRequest(options, providerName, out var request))
        {
            _cloudEgressAuthorizer.Authorize(request!);
        }
    }
}
