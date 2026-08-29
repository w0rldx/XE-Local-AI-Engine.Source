namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;

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
    private readonly IModelTrustResolver _modelTrustResolver;

    public RuntimeChatClient(IActiveCloudChatClientFactory activeCloudFactory,
        Func<IChatClient> localClientFactory,
        ICloudEgressAuthorizer cloudEgressAuthorizer,
        IModelTrustResolver modelTrustResolver)
    {
        ArgumentNullException.ThrowIfNull(activeCloudFactory);
        ArgumentNullException.ThrowIfNull(localClientFactory);
        ArgumentNullException.ThrowIfNull(cloudEgressAuthorizer);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);

        _activeCloudFactory = activeCloudFactory;
        _cloudEgressAuthorizer = cloudEgressAuthorizer;
        _modelTrustResolver = modelTrustResolver;
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
            AuthorizeDevelopmentLocalRequest(options, requestedModelId);
            return _localClient.Value;
        }

        AuthorizeDevelopmentCloudRequest(options, requestedModelId);
        return cloudClient;
    }

    /// <summary>
    ///     The fail-closed backstop on the LOCAL branch: a Development-marked request must never leave the trust
    ///     boundary through an external endpoint.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every existing per-send egress authorization lives on the cloud branch, because before external
    ///         providers the local branch could not egress. An <c>ext:</c> id breaks that assumption while staying on
    ///         the local branch by design (the orphan guard routes it there), so without this check a Development
    ///         attempt could reach a hosted endpoint with no authorization step having run at all.
    ///     </para>
    ///     <para>
    ///         This is a backstop, not the gate — <c>DevelopmentManagementService</c> and the coder/reviewer models
    ///         refuse the same models earlier and with better messages. It exists because this is the last point before
    ///         bytes go on the wire, and the classification it uses (the registry's cached generation) reports
    ///         UNRESOLVED rather than "fine" when it cannot answer.
    ///     </para>
    /// </remarks>
    private void AuthorizeDevelopmentLocalRequest(ChatOptions? options, string? requestedModelId)
    {
        if (!DevelopmentCloudAuthorizationMetadata.IsDevelopmentMarked(options))
        {
            return;
        }

        if (_modelTrustResolver.ClassifyExternalCached(requestedModelId) is { } trust && trust != ModelTrustLocality.Local)
        {
            throw new CloudEgressAuthorizationException("A Development request cannot be sent to an external model that is not declared local to this node's trust boundary.");
        }
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
