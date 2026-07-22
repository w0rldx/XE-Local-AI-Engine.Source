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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _buildService.ShutdownAsync(cancellationToken);
    }
}
