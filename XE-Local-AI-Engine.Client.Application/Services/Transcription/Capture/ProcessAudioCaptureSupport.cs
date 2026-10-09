namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

using System.Diagnostics;

/// <summary>
///     The one place the Windows build floor for WASAPI process loopback is decided. Pure, so the policy is testable
///     on every operating system rather than only on the one it describes.
/// </summary>
internal static class ProcessAudioCaptureSupport
{
    /// <summary>
    ///     Microsoft documents <c>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS</c> — the struct that actually carries the
    ///     loopback mode — as Windows Server 2022 / build 20348.
    /// </summary>
    /// <remarks>
    ///     NAudio annotates <c>WasapiRecorderBuilder.WithProcessLoopback</c> with
    ///     <c>[SupportedOSPlatform("windows10.0.19041.0")]</c>, but that is an ANALYZER annotation satisfying CA1416,
    ///     not compatibility evidence. The capability is gated on the documented number: being wrong this way hides a
    ///     feature, being wrong the other way is a hard COM failure the operator cannot act on.
    /// </remarks>
    internal const int MinimumWindowsBuild = 20348;

    /// <summary>
    ///     Whether an operating-system version clears the documented floor. Takes the version rather than reading
    ///     <see cref="Environment.OSVersion" /> so a test drives it; comparing <c>IsSupported</c> against its own
    ///     predicate would be a tautology that cannot fail.
    /// </summary>
    /// <remarks>
    ///     The minor component is deliberately absent: every Windows 10 and 11 release reports major 10, minor 0,
    ///     and an unused parameter is an IDE0060 build error here.
    /// </remarks>
    internal static bool IsBuildSupported(int major, int build) =>
        major > 10 || (major == 10 && build >= MinimumWindowsBuild);

    /// <summary>
    ///     Runs <paramref name="capture" /> until it ends on its own, <paramref name="cancellationToken" /> is cancelled, or
    ///     the target process exits, whichever comes first. Returns <see langword="true" /> when the target exited (or was
    ///     already gone, in which case <paramref name="capture" /> never runs).
    /// </summary>
    /// <remarks>
    ///     WASAPI process loopback yields no packets once its target is gone and its enumerator does not end, so without
    ///     this race a capture of a closed application ran until the operator stopped it. Returning normally on an exit
    ///     is what lets the coordinator end the session through the registry exactly once. Here rather than in the
    ///     Windows source so the race runs on every operating system's gate. A refused exit watch or a teardown failure
    ///     after the exit never fails the session; both are logged at Debug.
    /// </remarks>
    internal static async Task<bool> RunUntilProcessExitsAsync(int processId,
        Func<CancellationToken, Task> capture,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(capture);

        Process target;
        try
        {
            target = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (target)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var exited = WatchExitAsync(target.WaitForExitAsync(linked.Token), processId, logger, linked.Token);
            var captured = capture(linked.Token);

            var first = await Task.WhenAny(captured, exited);
            await linked.CancelAsync();
            if (first == exited && exited.IsCompletedSuccessfully)
            {
                try
                {
                    await captured;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Our own cancel, raised because the target exited.
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The recorder failed while it was torn down; the target is gone either way, so the session ends normally.
                    logger.LogDebug(exception, "The capture of process {ProcessId} failed while it stopped after the process exited.", processId);
                }

                return true;
            }

            try
            {
                await exited;
            }
            catch (OperationCanceledException)
            {
                // The watch is torn down with the capture; it never reports anything itself.
            }

            await captured;
            return false;
        }
    }

    // Completes when the target exits. A watch the OS refuses (a protected process denies the handle) never completes
    // instead of faulting, so the capture alone decides when the session ends; the caller's teardown cancels it.
    private static async Task WatchExitAsync(Task waitForExit, int processId, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await waitForExit;
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Process {ProcessId} cannot be watched for its exit; the capture runs until it ends or is stopped.", processId);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
