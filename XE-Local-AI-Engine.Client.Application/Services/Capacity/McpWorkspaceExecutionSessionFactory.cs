namespace XE_Local_AI_Engine.Client.Services.Capacity;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Acquires the owner-node single-flight lease, creates or attaches the established AgentHome sandbox, and replaces
///     its selected root with exactly one workspace resolved from an opaque id under that lease. No host path is retained
///     by the returned session, and every post-lease failure is recovered or poisons the owner-node key before release.
/// </summary>
internal sealed class McpWorkspaceExecutionSessionFactory : IMcpWorkspaceExecutionSessionFactory
{
    private readonly ComputeOptions _ceilingDefaults;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly IAgentHomeWorkspaceIsolation _isolation;
    private readonly IAgentHomeExecutionLeaseManager _leaseManager;
    private readonly ILogger<McpWorkspaceExecutionSessionFactory> _logger;
    private readonly IAgentHomeManifestService _manifestService;
    private readonly AgentHomeOptions _options;
    private readonly IAgentSandboxRuntimeProvider _provider;
    private readonly SandboxOptions _sandboxOptions;
    private readonly ISelectedFolderResolver _resolver;
    private readonly IAgentHomeWorkspaceService _workspaceService;

    public McpWorkspaceExecutionSessionFactory(IAgentHomeIdentityProvider identityProvider,
        IAgentHomeExecutionLeaseManager leaseManager,
        IAgentHomeWorkspaceIsolation isolation,
        IAgentHomeManifestService manifestService,
        IAgentSandboxRuntimeProvider provider,
        ISelectedFolderResolver resolver,
        IAgentHomeWorkspaceService workspaceService,
        IOptions<AgentHomeOptions> options,
        IOptions<SandboxOptions> sandboxOptions,
        IOptions<ComputeOptions> ceilingDefaults,
        ILogger<McpWorkspaceExecutionSessionFactory> logger)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
        _isolation = isolation ?? throw new ArgumentNullException(nameof(isolation));
        _manifestService = manifestService ?? throw new ArgumentNullException(nameof(manifestService));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _sandboxOptions = (sandboxOptions ?? throw new ArgumentNullException(nameof(sandboxOptions))).Value;
        _ceilingDefaults = (ceilingDefaults ?? throw new ArgumentNullException(nameof(ceilingDefaults))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<McpWorkspaceExecutionSessionOpenResult> OpenAsync(Guid workspaceId,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty)
        {
            return WorkspaceNotAuthorized();
        }

        var identity = await _identityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var leaseKey = new AgentHomeExecutionLeaseKey(identity.OwnerUserId, identity.NodeId);
        var lease = _leaseManager.TryAcquire(leaseKey);
        if (lease is null)
        {
            return _leaseManager.IsPoisoned(leaseKey)
                ? WorkspacePreparationFailed()
                : McpWorkspaceExecutionSessionOpenResult.Rejected(McpExecutionFailureCodes.WorkspaceBusy,
                    "Cannot run: the selected workspace is busy.");
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
            _ = await _manifestService.InitializeAsync(attachKey, cancellationToken).ConfigureAwait(false);
            var handle = await _provider.CreateOrAttachAsync(new SandboxCreateRequest
                {
                    AttachKey = attachKey,
                    RuntimeProfile = _options.DefaultRuntimeProfile,
                    // The same two decisions AgentHome's own create site makes, through the same two helpers rather
                    // than re-derived here: this jail is AgentHome's substrate under a different lease holder, and a
                    // work session that quietly resolved a different posture would be the drift those helpers exist to
                    // stop. It is constrained by the AgentHome section, so it reads that section's switch.
                    NetworkPolicy = SandboxEgressPolicy.Resolve(_provider.Capabilities,
                        _sandboxOptions.RequireEgressDenial,
                        SandboxEgressPolicy.AgentOptionKey,
                        SandboxWorkloads.WorkSession.Workload),
                    ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.WorkSession, _provider.Capabilities, _ceilingDefaults)
                },
                cancellationToken).ConfigureAwait(false);

            ResolvedSelectedFolder workspace;
            try
            {
                workspace = await _resolver.ResolveAsync(workspaceId.ToString("D"), cancellationToken).ConfigureAwait(false);
                if (workspace.Id != workspaceId)
                {
                    throw new SelectedFolderValidationException("The selected workspace is not active.");
                }
            }
            catch (SelectedFolderValidationException)
            {
                return await RejectAfterRecoveryAsync(attachKey, leaseKey, WorkspaceNotAuthorized()).ConfigureAwait(false);
            }

            var snapshots = await _workspaceService.PrepareSelectedFoldersAsync(handle, [workspace], cancellationToken).ConfigureAwait(false);
            if (!HasExactCopiedWorkspace(snapshots, workspace))
            {
                return await RejectAfterRecoveryAsync(attachKey, leaseKey, WorkspacePreparationFailed()).ConfigureAwait(false);
            }

            return SuccessWithTransferredLease(lease);
        }
        catch (OperationCanceledException)
        {
            _ = await TryRecoverAsync(attachKey, leaseKey, workspaceId).ConfigureAwait(false);
            lease.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError("MCP workspace preparation failed for workspace {WorkspaceId}; failure type {FailureType}.",
                workspaceId,
                exception.GetType().Name);
            return await RejectAfterRecoveryAsync(attachKey, leaseKey, WorkspacePreparationFailed()).ConfigureAwait(false);
        }

        async Task<McpWorkspaceExecutionSessionOpenResult> RejectAfterRecoveryAsync(SandboxAttachKey failedAttachKey,
            AgentHomeExecutionLeaseKey failedLeaseKey,
            McpWorkspaceExecutionSessionOpenResult rejection)
        {
            var recovered = await TryRecoverAsync(failedAttachKey, failedLeaseKey, workspaceId).ConfigureAwait(false);
            lease.Dispose();
            return recovered ? rejection : WorkspacePreparationFailed();
        }
    }

    private async Task<bool> TryRecoverAsync(SandboxAttachKey attachKey,
        AgentHomeExecutionLeaseKey leaseKey,
        Guid workspaceId)
    {
        try
        {
            await _isolation.RecoverExistingAsync(attachKey, leaseKey, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError("MCP workspace recovery failed for workspace {WorkspaceId}; failure type {FailureType}.",
                workspaceId,
                exception.GetType().Name);
            return false;
        }
    }

    private static McpWorkspaceExecutionSessionOpenResult WorkspaceNotAuthorized() =>
        McpWorkspaceExecutionSessionOpenResult.Rejected(McpExecutionFailureCodes.WorkspaceNotAuthorized,
            "Cannot run: the selected workspace is not authorized.");

    private static McpWorkspaceExecutionSessionOpenResult WorkspacePreparationFailed() =>
        McpWorkspaceExecutionSessionOpenResult.Rejected(McpExecutionFailureCodes.WorkspacePreparationFailed,
            "Cannot run: the selected workspace could not be prepared safely.");

    private static bool HasExactCopiedWorkspace(IReadOnlyList<SelectedFolderSnapshot> snapshots,
        ResolvedSelectedFolder workspace)
    {
        if (snapshots.Count != 1)
        {
            return false;
        }

        var snapshot = snapshots[0];
        return snapshot.Status == SelectedFolderCopyStatus.Copied
               && string.Equals(snapshot.Alias, workspace.Alias, StringComparison.Ordinal)
               && string.Equals(snapshot.WorkspacePath, $"workspace/selected/{workspace.Alias}", StringComparison.Ordinal);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The successful execution session takes ownership of the lease and exposes that ownership through IDisposable.")]
    private static McpWorkspaceExecutionSessionOpenResult SuccessWithTransferredLease(IAgentHomeExecutionLease lease) =>
        McpWorkspaceExecutionSessionOpenResult.Success(new Session(lease));

    private sealed class Session(IAgentHomeExecutionLease lease) : IMcpWorkspaceExecutionSession
    {
        private IAgentHomeExecutionLease? _lease = lease;

        public IDisposable EnterAmbientScope()
        {
            return (_lease ?? throw new ObjectDisposedException(nameof(Session))).EnterAmbientScope();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }
}
