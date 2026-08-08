namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using XE_Local_AI_Engine.Client.Services.Sandbox;

internal sealed class AgentHomeWorkspaceIsolation : IAgentHomeWorkspaceIsolation
{
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly IAgentHomeExecutionLeaseManager _leases;
    private readonly ILogger<AgentHomeWorkspaceIsolation> _logger;
    private readonly IAgentSandboxRuntimeProvider _provider;

    public AgentHomeWorkspaceIsolation(IAgentSandboxRuntimeProvider provider,
        IAgentHomeExecutionLeaseManager leases,
        ILogger<AgentHomeWorkspaceIsolation> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgentHomeWorkspaceClearResult> ClearAsync(SandboxHandle handle,
        AgentHomeExecutionLeaseKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            using var resetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resetCts.CancelAfter(RecoveryTimeout);
            await _provider.ResetDirectoryAsync(handle, AgentHomeGit.WorkspaceSelectedRoot, resetCts.Token).ConfigureAwait(false);
            _leases.ClearPoison(key);
            return AgentHomeWorkspaceClearResult.Reset;
        }
        catch (Exception resetException) when (resetException is not OutOfMemoryException)
        {
            _logger.LogWarning(resetException, "Selected workspace reset failed for sandbox {SandboxId}; killing it.", handle.SandboxId);
            try
            {
                using var killCts = new CancellationTokenSource(RecoveryTimeout);
                await _provider.KillAsync(handle, killCts.Token).ConfigureAwait(false);
                _leases.ClearPoison(key);
                return AgentHomeWorkspaceClearResult.SandboxKilled;
            }
            catch (Exception killException) when (killException is not OutOfMemoryException)
            {
                _leases.MarkPoisoned(key);
                _logger.LogError(killException, "Selected workspace reset and sandbox kill both failed; owner-node is poisoned.");
                throw new AgentHomeWorkspacePoisonedException();
            }
        }
    }

    public async Task RecoverExistingAsync(SandboxAttachKey attachKey,
        AgentHomeExecutionLeaseKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachKey);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(RecoveryTimeout);
            var handle = await _provider.ConnectAsync(attachKey, connectCts.Token).ConfigureAwait(false);
            _ = await ClearAsync(handle, key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SandboxHandleInvalidException)
        {
            _leases.ClearPoison(key);
        }
        catch (AgentHomeWorkspacePoisonedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _leases.MarkPoisoned(key);
            _logger.LogError(exception, "Could not recover the existing AgentHome sandbox; owner-node is poisoned.");
            throw new AgentHomeWorkspacePoisonedException();
        }
    }
}
