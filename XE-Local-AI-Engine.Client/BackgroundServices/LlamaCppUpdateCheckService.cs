namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Runs ONE llama.cpp runtime update check per app start, off the startup path: after a short delay (so host
///     start-up is never blocked) it resolves the recommended tag from <see cref="INodeRuntimeSettings" />, confirms it
///     against the live release catalog, reads the installed tag from <see cref="IInstalledRuntimeStore" />, and records
///     the result in <see cref="ILlamaCppUpdateState" /> so the runtime-status endpoint can surface "update available"
///     without re-hitting the live API on every poll.
/// </summary>
/// <remarks>
///     <para>
///         <b>Offline-tolerant.</b> An unreachable / rate-limited catalog produces an <c>isOffline</c> snapshot with no
///         update advertised — never a crash. Any unexpected error is caught and logged; the empty snapshot stays.
///     </para>
///     <para>
///         <b>Notify-only, once.</b> This service never downloads or installs a binary — install is always
///         operator-initiated via the update endpoint. It is decoupled from any app-package updater channel.
///     </para>
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
    private readonly ILlamaCppUpdateState _updateState;

    public LlamaCppUpdateCheckService(INodeRuntimeSettings nodeRuntimeSettings,
        ILlamaCppReleaseCatalog catalog,
        IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppUpdateState updateState,
        ILogger<LlamaCppUpdateCheckService> logger)
        : this(nodeRuntimeSettings, catalog, installedRuntimeStore, updateState, logger, DefaultStartupDelay)
    {
    }

    // Test seam: injects the startup delay so the one-shot check can be exercised without a 10s wait.
    internal LlamaCppUpdateCheckService(INodeRuntimeSettings nodeRuntimeSettings,
        ILlamaCppReleaseCatalog catalog,
        IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppUpdateState updateState,
        ILogger<LlamaCppUpdateCheckService> logger,
        TimeSpan startupDelay)
    {
        _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupDelay = startupDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupDelay, stoppingToken).ConfigureAwait(false);
            }

            await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
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
            var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(cancellationToken).ConfigureAwait(false);
            var installed = await _installedRuntimeStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            var installedTag = installed?.Tag;

            var recommendedResult = await _catalog.ResolveRecommendedAsync(recommendedTag, cancellationToken).ConfigureAwait(false);

            // No live data (offline / rate-limited / unresolved) — record an offline snapshot, advertise no update.
            if (recommendedResult.HasNoLiveData || recommendedResult.Tag is null)
            {
                _updateState.Store(new LlamaCppUpdateSnapshot(
                    installedTag,
                    RecommendedTag: recommendedTag,
                    UpstreamLatestTag: null,
                    UpdateAvailable: false,
                    IsOffline: recommendedResult.IsOffline || recommendedResult.IsRateLimited,
                    CheckedAtUtc: DateTimeOffset.UtcNow));
                return;
            }

            var resolvedRecommended = recommendedResult.Tag;

            // An update is available only when the recommended tag is resolvable AND it differs from the installed one.
            // A fresh node (no installed state) is "update available" so the operator can install the recommended build.
            var updateAvailable = !string.Equals(installedTag, resolvedRecommended, StringComparison.Ordinal);

            _updateState.Store(new LlamaCppUpdateSnapshot(
                installedTag,
                RecommendedTag: resolvedRecommended,
                UpstreamLatestTag: null,
                updateAvailable,
                IsOffline: false,
                CheckedAtUtc: DateTimeOffset.UtcNow));
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
