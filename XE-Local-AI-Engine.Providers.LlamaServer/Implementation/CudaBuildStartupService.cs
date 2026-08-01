namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Startup <see cref="IHostedService" /> for source builds: (1) reconciles stale work and swap directories left by a
///     host crash/kill mid-build (<c>[archLOW-1]</c>), and (2) seeds the cached active-source signal from the
///     installed-runtime record so a previously-adopted source build is selected after a restart without a per-call store
///     read. Reconciliation failure is fatal to startup so readiness cannot be reported against ambiguous runtime state.
/// </summary>
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
            // The host's shutdown token means "stop being graceful", NOT "throw". ShutdownAsync awaits the start
            // gate and the in-flight build on this token, so once the shutdown budget (HostOptions.ShutdownTimeout,
            // 30s by default) expires every one of those awaits throws. Host.StopAsync aggregates whatever a
            // StopAsync throws and rethrows it, so letting it escape turned an over-budget-but-otherwise-normal
            // shutdown into an UNHANDLED exception and a non-zero exit code — observed live on 2026-08-01 as
            // "One or more hosted services failed to stop" after a model had been loaded in desktop mode.
            // Abandoning the drain is the correct response here; the build's own cancellation has already been
            // signalled and its work tree is reconciled by RecoverAsync on the next start.
            _logger.LogWarning("The managed source-build shutdown drain was cut short by the host shutdown budget; "
                               + "any in-flight build is abandoned and will be reconciled on the next start.");
        }
    }
}
