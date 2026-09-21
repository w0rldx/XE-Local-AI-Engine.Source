namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Reconciles the managed source-build state before the host becomes ready, and drains an in-flight build on the
///     way out.
/// </summary>
/// <remarks>
///     Recovery failure is fatal on purpose: continuing with an ambiguous adoption journal could expose an unverified runtime as this
///     node's transcription engine. This is also what republishes the managed-runtime signal at start — the backend selector trusts a
///     managed CUDA build only once something has set that signal, so without this service an adopted build stops resolving after a
///     restart and the node silently falls back to CPU.
/// </remarks>
internal sealed class WhisperCppSourceBuildLifecycle : IHostedService
{
    private readonly IWhisperCppSourceBuildService _service;
    private readonly ILogger<WhisperCppSourceBuildLifecycle> _logger;

    public WhisperCppSourceBuildLifecycle(
        IWhisperCppSourceBuildService service,
        ILogger<WhisperCppSourceBuildLifecycle> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to recover the managed whisper.cpp source-build state.");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's shutdown token means "stop being graceful", not "throw". ShutdownAsync awaits the start gate on this token, so an over-budget shutdown would otherwise surface as an unhandled
            // exception out of Host.StopAsync and kill the process instead of letting it exit cleanly. The abandoned build is reconciled on the next start, which is exactly what the journal is for.
            _logger.LogWarning("The managed whisper.cpp shutdown drain was cut short by the host shutdown budget; any "
                              + "in-flight build is abandoned and will be reconciled on the next start.");
        }
    }
}
