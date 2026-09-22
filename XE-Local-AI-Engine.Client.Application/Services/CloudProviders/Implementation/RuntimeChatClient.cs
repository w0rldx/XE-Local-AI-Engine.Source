namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     The node's registered <see cref="IChatClient" />: a stable wrapper that re-selects cloud-vs-local on
///     <b>every</b> call.
/// </summary>
/// <remarks>
///     Singleton consumers (the agent factories) capture this wrapper once, but each send re-evaluates the active provider
///     via <see cref="IActiveCloudChatClientFactory" />, so a runtime sign-in or sign-out takes effect on the next send with
///     no node restart. The local model client is stable and is resolved once and reused; the cloud client is resolved per
///     call but cached on a selection fingerprint, so it is rebuilt only when the selection changes. A cloud provider that
///     is selected but unusable (no Codex session) throws a typed re-auth error rather than silently routing local.
/// </remarks>
public sealed class RuntimeChatClient : IChatClient
{
    /// <summary>Why the resolved client is never disposed at this boundary.</summary>
    /// <remarks>
    ///     The client returned per call is either the cached local client (owned by this wrapper, disposed in
    ///     <see cref="Dispose" />) or the active cloud client, owned and lifecycle-managed by
    ///     <see cref="IActiveCloudChatClientFactory" />, which caches it and does NOT dispose swapped-out wrappers, so an
    ///     in-flight request is never torn down. Disposing here would be incorrect for both.
    /// </remarks>
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

        // A metadata lookup carries no model id. Keep a node-managed invocation on the local branch without applying
        // the exact-model send check; streaming and non-streaming dispatch still validate their ChatOptions.ModelId.
        if (NodeManagedLlamaRoutingScope.CurrentModel is not null)
        {
            return _localClient.Value.GetService(serviceType, serviceKey);
        }

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
        if (NodeManagedLlamaRoutingScope.CurrentModel is { } requiredModel)
        {
            if (!string.Equals(requestedModelId, requiredModel, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The invocation model changed after node-managed llama routing was required.");
            }

            AuthorizeDevelopmentLocalRequest(options, requestedModelId);
            return _localClient.Value;
        }

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
    ///     Per-send egress authorization otherwise lives on the cloud branch: before external providers the local branch could not egress. An <c>ext:</c> id
    ///     stays on the local branch by design (the orphan guard routes it there), so without this check a Development attempt could reach a hosted endpoint
    ///     with no authorization step run at all. A backstop, not the gate: <c>DevelopmentManagementService</c> and the coder/reviewer models refuse the same
    ///     models earlier. This is the last point before bytes go on the wire, and its classification reports UNRESOLVED, never "fine", when it cannot answer.
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
