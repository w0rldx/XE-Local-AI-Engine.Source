namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>Clears the shared AgentHome selected root before an operator workspace reference is revoked.</summary>
internal sealed class AgentHomeWorkspaceRevocationPreparation : IWorkspaceRevocationPreparation
{
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly IAgentHomeWorkspaceIsolation _isolation;
    private readonly IAgentHomeExecutionLeaseManager _leaseManager;
    private readonly AgentHomeOptions _options;
    private readonly IAgentSandboxRuntimeProvider _provider;

    public AgentHomeWorkspaceRevocationPreparation(IAgentHomeIdentityProvider identityProvider,
        IAgentHomeExecutionLeaseManager leaseManager,
        IAgentHomeWorkspaceIsolation isolation,
        IAgentSandboxRuntimeProvider provider,
        IOptions<AgentHomeOptions> options)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
        _isolation = isolation ?? throw new ArgumentNullException(nameof(isolation));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<IWorkspaceRevocationSession> PrepareAsync(ResolvedSelectedFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var key = new AgentHomeExecutionLeaseKey(identity.OwnerUserId, identity.NodeId);
        var lease = _leaseManager.TryAcquireForRecovery(key);
        if (lease is null)
        {
            throw new WorkspaceRevocationBusyException();
        }

        var attachKey = new SandboxAttachKey
        {
            OwnerUserId = identity.OwnerUserId,
            NodeId = identity.NodeId,
            ProviderName = _provider.ProviderName,
            RuntimeProfile = _options.DefaultRuntimeProfile,
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };

        try
        {
            await _isolation.RecoverExistingAsync(attachKey, key, cancellationToken).ConfigureAwait(false);
            return new WorkspaceRevocationSession(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private sealed class WorkspaceRevocationSession : IWorkspaceRevocationSession
    {
        private IAgentHomeExecutionLease? _lease;

        public WorkspaceRevocationSession(IAgentHomeExecutionLease lease)
        {
            _lease = lease;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
