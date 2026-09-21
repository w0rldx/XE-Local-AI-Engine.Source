namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Startup <see cref="IHostedService" /> for source builds: it reconciles stale work and swap directories left by a
///     host crash or kill mid-build (<c>[archLOW-1]</c>), and seeds the cached active-source signal from the
///     installed-runtime record.
/// </summary>
/// <remarks>
///     Seeding is what lets a previously-adopted source build be selected after a restart without a per-call store
///     read. Reconciliation failure is fatal to startup, so readiness cannot be reported against ambiguous runtime
///     state.
/// </remarks>
internal sealed class CudaBuildStartupService : IHostedService
{
    private readonly ILlamaCppSourceBuildService _buildService;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILogger<CudaBuildStartupService> _logger;
    private readonly ICudaManagedBuildSignal _signal;

    public CudaBuildStartupService(ILlamaCppSourceBuildService buildService,
        IInstalledRuntimeStore installedRuntimeStore,
        ICudaManagedBuildSignal signal,
        ILogger<CudaBuildStartupService> logger)
    {
        _buildService = buildService ?? throw new ArgumentNullException(nameof(buildService));
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _buildService.RecoverAsync(cancellationToken).ConfigureAwait(false);

            var installed = await _installedRuntimeStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (installed?.SourceBuildPath is { Length: > 0 } sourceBuildPath
                && File.Exists(Path.Combine(sourceBuildPath, "llama-server")))
            {
                // Optimistic seed: the serve-time validator (EnsureBinaryAsync) re-checks perms/SHA and clears the signal
                // if the build is actually invalid, so seeding on presence alone is safe.
                _signal.SetActive(installed.Variant);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Reconciling the managed source-build state at startup failed.");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _buildService.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's shutdown token means "stop being graceful", NOT "throw": ShutdownAsync awaits the start gate and the in-flight build on it, so past the
            // shutdown budget those awaits throw, Host.StopAsync rethrows, and a normal shutdown exits non-zero. Abandoning the drain is correct — see RecoverAsync.
            _logger.LogWarning("The managed source-build shutdown drain was cut short by the host shutdown budget; "
                               + "any in-flight build is abandoned and will be reconciled on the next start.");
        }
    }
}
