namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
///     Reconciles a runtime's managed source-build state before the host becomes ready, and drains an in-flight build
///     on the way out.
/// </summary>
/// <remarks>
///     Recovery failure is fatal on purpose: continuing with an ambiguous adoption journal could expose an unverified runtime as this
///     node's engine. This is also what republishes the managed-runtime signal at start — a backend selector trusts a managed GPU build
///     only once something has set that signal, so without this service an adopted build stops resolving after a restart and the node
///     silently falls back to CPU.
/// </remarks>
public abstract class ManagedSourceBuildLifecycle : IHostedService
{
    private readonly Func<CancellationToken, Task> _recoverAsync;
    private readonly Func<CancellationToken, Task> _shutdownAsync;
    private readonly ILogger _logger;
    private readonly string _runtimeName;

    /// <param name="runtimeName">The runtime as log lines name it, such as <c>whisper.cpp</c>.</param>
    protected ManagedSourceBuildLifecycle(Func<CancellationToken, Task> recoverAsync,
        Func<CancellationToken, Task> shutdownAsync,
        ILogger logger,
        string runtimeName)
    {
        _recoverAsync = recoverAsync;
        _shutdownAsync = shutdownAsync;
        _logger = logger;
        _runtimeName = runtimeName;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _recoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to recover the managed {Runtime} source-build state.", _runtimeName);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _shutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's shutdown token means "stop being graceful", not "throw". ShutdownAsync awaits the start gate on this token, so an over-budget shutdown would otherwise surface as an unhandled
            // exception out of Host.StopAsync and kill the process instead of letting it exit cleanly. The abandoned build is reconciled on the next start, which is exactly what the journal is for.
            _logger.LogWarning("The managed {Runtime} shutdown drain was cut short by the host shutdown budget; any "
                               + "in-flight build is abandoned and will be reconciled on the next start.",
                _runtimeName);
        }
    }
}
