namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using XE_Local_AI_Engine.Client.Services.LlamaCpp;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Runs ONE llama.cpp runtime update check per app start, off the startup path, recording the result in
///     <see cref="ILlamaCppUpdateState" /> so the runtime-status endpoint surfaces "update available" without
///     re-hitting the live API on every poll.
/// </summary>
/// <remarks>
///     It resolves the recommended tag from <see cref="INodeRuntimeSettings" />, confirms it against the live release
///     catalog and reads the installed tag from <see cref="IInstalledRuntimeStore" />. <b>Offline-tolerant:</b> an
///     unreachable or rate-limited catalog produces an <c>isOffline</c> snapshot advertising no update, never a crash;
///     any unexpected error is caught and logged and the empty snapshot stays. <b>Notify-only, once:</b> it never
///     downloads or installs a binary — install is operator-initiated via the update endpoint, not an app-package updater.
/// </remarks>
public sealed class LlamaCppUpdateCheckService : BackgroundService
{
    // A short, non-blocking delay so the host finishes coming up before the (network) catalog probe runs.
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(10);

    private readonly ILlamaCppReleaseCatalog _catalog;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILogger<LlamaCppUpdateCheckService> _logger;
    private readonly INodeRuntimeSettings _nodeRuntimeSettings;
    private readonly TimeSpan _startupDelay;
    private readonly TimeProvider _timeProvider;
    private readonly ILlamaCppUpdateState _updateState;

    public LlamaCppUpdateCheckService(INodeRuntimeSettings nodeRuntimeSettings,
        ILlamaCppReleaseCatalog catalog,
        IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppUpdateState updateState,
        TimeProvider timeProvider,
        ILogger<LlamaCppUpdateCheckService> logger)
        : this(nodeRuntimeSettings, catalog, installedRuntimeStore, updateState, timeProvider, logger, DefaultStartupDelay)
    {
    }

    // Test seam: injects the startup delay so the one-shot check can be exercised without a 10s wait.
    internal LlamaCppUpdateCheckService(INodeRuntimeSettings nodeRuntimeSettings,
        ILlamaCppReleaseCatalog catalog,
        IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppUpdateState updateState,
        TimeProvider timeProvider,
        ILogger<LlamaCppUpdateCheckService> logger,
        TimeSpan startupDelay)
    {
        _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupDelay = startupDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupDelay, stoppingToken);
            }

            // The gate goes AFTER the startup delay so a node parked on an undecided profile is not also holding the
            // delay open, and BEFORE the one-shot check so CheckOnceAsync stays the pure check.
            await ExternalAccessGate.WaitUntilDecidedAsync(_nodeRuntimeSettings, _timeProvider, stoppingToken);
            if (!await _nodeRuntimeSettings.GetAutoCheckRuntimeUpdatesAsync(stoppingToken))
            {
                _logger.LogDebug("The automatic llama.cpp runtime update check is disabled by the node's external-access settings.");
                return;
            }

            await CheckOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during the delay or the check — nothing to record.
        }
    }

    // Internal so a test can drive the one-shot check directly (no delay, deterministic).
    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(cancellationToken);
            var installed = await _installedRuntimeStore.ReadAsync(cancellationToken);
            var installedTag = installed?.Tag;

            var recommendedResult = await _catalog.ResolveRecommendedAsync(recommendedTag, cancellationToken);

            // Also resolve the true upstream-latest tag so developer mode has it on the startup snapshot, with no ?refresh
            // round-trip (mirrors GetLlamaCppRuntimeEndpoint.ComputeFreshSnapshotAsync); no live data yields a null tag, never a throw.
            var upstreamResult = await _catalog.ResolveUpstreamLatestAsync(cancellationToken);

            // No live data (offline / rate-limited / unresolved) — record an offline snapshot, advertise no update.
            if (recommendedResult.HasNoLiveData || recommendedResult.Tag is null)
            {
                _updateState.Store(new LlamaCppUpdateSnapshot
                {
                    InstalledTag = installedTag,
                    RecommendedTag = recommendedTag,
                    UpstreamLatestTag = upstreamResult.Tag,
                    UpdateAvailable = false,
                    IsOffline = recommendedResult.IsOffline || recommendedResult.IsRateLimited,
                    CheckedAtUtc = _timeProvider.GetUtcNow()
                });
                return;
            }

            var resolvedRecommended = recommendedResult.Tag;

            // An update is available only when the resolvable recommended tag is NEWER than the installed one; a fresh node
            // (no installed state) counts as available. LlamaCppRuntimeTag.IsUpdateAvailable encodes both, and never a downgrade.
            var updateAvailable = LlamaCppRuntimeTag.IsUpdateAvailable(installedTag, resolvedRecommended);

            _updateState.Store(new LlamaCppUpdateSnapshot
            {
                InstalledTag = installedTag,
                RecommendedTag = resolvedRecommended,
                UpstreamLatestTag = upstreamResult.Tag,
                UpdateAvailable = updateAvailable,
                IsOffline = false,
                CheckedAtUtc = _timeProvider.GetUtcNow()
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never crash startup over an update check — leave the prior/empty snapshot and log for diagnostics.
            _logger.LogWarning(exception, "The llama.cpp runtime update check could not complete.");
        }
    }
}
