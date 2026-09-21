namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Operator-initiated removal of one run directory, gated exactly as the retention sweep's removals are.
/// </summary>
/// <remarks>
///     No grace window, unlike the sweep: that window buys an unattended walk a margin no one is watching, while a
///     delete an operator asked for is covered by two reads taken immediately before the removal — the execution
///     lease for a run that is still executing, and <see cref="AgentHomeRunApplyGuard" /> for one whose patch is
///     being applied. The lease is read there rather than at entry because a run can start while the path is
///     being resolved.
/// </remarks>
internal sealed partial class AgentHomeRunDeleteService : IAgentHomeRunDeleteService
{
    private readonly AgentHomeRunApplyGuard _applyGuard;
    private readonly string _dataDirectoryRoot;
    private readonly IAgentHomeIdentityProvider _identityProvider;
    private readonly IAgentHomeExecutionLeaseManager _leaseManager;
    private readonly ILogger<AgentHomeRunDeleteService> _logger;
    private readonly AgentHomeOptions _options;

    public AgentHomeRunDeleteService(IOptions<AgentHomeOptions> options,
        INodeDataDirectory dataDirectory,
        IAgentHomeIdentityProvider identityProvider,
        IAgentHomeExecutionLeaseManager leaseManager,
        AgentHomeRunApplyGuard applyGuard,
        ILogger<AgentHomeRunDeleteService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _options = options.Value;
        _dataDirectoryRoot = dataDirectory.Root;
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
        _applyGuard = applyGuard ?? throw new ArgumentNullException(nameof(applyGuard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AgentHomeRunDeleteOutcome> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        var runsRoot = AgentHomeRunPaths.ResolveRunsRoot(_options, _dataDirectoryRoot);
        if (AgentHomeRunPaths.TryResolveRun(runsRoot, runId) is not { } run)
        {
            return AgentHomeRunDeleteOutcome.NotFound;
        }

        // Identity first, so the lease read below is the LAST thing before the removal. Without it there is no lease
        // key, so there is no way to tell an in-flight run from a finished one — and a delete that cannot ask refuses.
        var identity = await _identityProvider.GetAsync(cancellationToken);
        if (_leaseManager.IsHeld(new AgentHomeExecutionLeaseKey(identity.OwnerUserId, identity.NodeId)))
        {
            return AgentHomeRunDeleteOutcome.Conflict;
        }

        try
        {
            // Recursive delete never follows a link out of the tree, and the gate already proved this is not one.
            // Inside the apply guard, so an apply of THIS run can neither start here nor lose its outcome entry.
            if (!_applyGuard.TryRemove(run.RunId, () => Directory.Delete(run.Path, recursive: true)))
            {
                return AgentHomeRunDeleteOutcome.Conflict;
            }

            RunDeleted(_logger, run.RunId);
            return AgentHomeRunDeleteOutcome.Deleted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeleteFailed(_logger, run.RunId, exception);
            return AgentHomeRunDeleteOutcome.Conflict;
        }
    }

    [LoggerMessage(EventId = 4820, Level = LogLevel.Information,
        Message = "An operator deleted AgentHome run {RunId}; its logs and exported patch are gone.")]
    private static partial void RunDeleted(ILogger logger, string runId);

    [LoggerMessage(EventId = 4821, Level = LogLevel.Warning, Message = "AgentHome run {RunId} could not be deleted.")]
    private static partial void DeleteFailed(ILogger logger, string runId, Exception exception);
}
