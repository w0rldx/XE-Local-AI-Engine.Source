namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Ticks residency when a registered runtime child dies on its own, instead of when a reaper next notices the corpse.</summary>
public static class ResidencyExitNotification
{
    /// <summary>
    ///     Awaits <paramref name="exit" />, then ticks <paramref name="notifier" />. Never faults: it is fire-and-forget.
    /// </summary>
    /// <remarks>
    ///     A deliberate teardown completes the wait too; that extra tick is harmless. Shutdown (a cancelled or disposed wait) ends
    ///     it silently. Any other failure leaves the child's state unknown, so it logs at Debug and ticks anyway: a re-read is the safe answer.
    /// </remarks>
    /// <param name="processName">The child's executable name for the Debug line, such as <c>whisper-server</c>.</param>
    public static async Task TickOnExitAsync(Task<bool> exit, IRuntimeResidencyChangeNotifier notifier, ILogger logger, string processName)
    {
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            await exit.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogDebug("The {ProcessName} exit wait failed ({ErrorClass}); ticking residency anyway.", processName, exception.GetType().Name);
        }

        notifier.NotifyChanged();
    }
}
