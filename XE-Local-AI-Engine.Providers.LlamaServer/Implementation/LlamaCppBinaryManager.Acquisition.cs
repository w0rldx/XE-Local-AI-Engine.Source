namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Acquisition-visibility branch of <see cref="LlamaCppBinaryManager" />. The manager runs off the startup path while
///     a fully-rendered, idle-looking UI is already on screen, so without a progress channel a slow first-run download is
///     indistinguishable from a broken one. This partial owns the reporting side-channel: a small per-acquisition
///     reporter that stamps the (variant, tag, step-count) context onto every status write so the call sites stay
///     one-liners.
/// </summary>
public sealed partial class LlamaCppBinaryManager
{
    /// <summary>
    ///     The cudart companion archive is always the SECOND archive of a Windows-CUDA acquisition (the build itself is
    ///     the first), so its progress reports under a fixed step index rather than one threaded through from the caller.
    /// </summary>
    private const int CudartStepIndex = 2;

    /// <summary>
    ///     Per-acquisition status reporter. Carries the constant context (variant, tag, how many archives this
    ///     acquisition fetches) so each call site only names the phase, the step, and — while downloading — the byte
    ///     counters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Silence is the default.</b> A cache-hit serve happens on EVERY model spawn, so an acquisition that
    ///         acquired nothing must emit nothing: <see cref="Complete" /> and <see cref="Fail" /> are suppressed unless
    ///         at least one non-terminal <see cref="Report" /> already went out. Without that guard the hub would carry a
    ///         <c>Completed</c> per spawn and the banner would flicker on a warm cache.
    ///     </para>
    ///     <para>
    ///         <b>Sanitization.</b> <see cref="LlamaRuntimeException" /> messages are user-safe by contract (the manager
    ///         constructs them precisely so no path, URL, or token leaks). Any other exception — an
    ///         <see cref="IOException" /> naming the temp file, an <see cref="HttpRequestException" /> naming the host —
    ///         is collapsed to a generic reason rather than surfaced verbatim.
    ///     </para>
    /// </remarks>
    private sealed class AcquisitionReporter(IRuntimeAcquisitionStatusRegistry? registry, GpuVariant variant, string? tag, int stepCount)
    {
        /// <summary>The user-safe stand-in for an exception whose message is not sanitized by contract.</summary>
        private const string GenericFailureReason =
            "The llama.cpp runtime could not be downloaded. Check the network connection and try again.";

        private bool _reported;

        /// <summary>
        ///     Records one non-terminal status. A no-op when no registry was injected (provider-only / test hosts), which
        ///     keeps those hosts byte-behavior-identical.
        /// </summary>
        public void Report(RuntimeAcquisitionPhase phase, int stepIndex, long? completedBytes = null, long? totalBytes = null)
        {
            if (registry is null)
            {
                return;
            }

            // Only a real report arms the terminal statuses — see the cache-hit note on the type.
            _reported = true;
            registry.Report(new RuntimeAcquisitionUpdate(phase,
                variant.ToString(),
                tag,
                completedBytes,
                totalBytes,
                stepIndex,
                stepCount));
        }

        /// <summary>Closes a reported acquisition as succeeded. Silent when nothing was acquired (cache hit).</summary>
        public void Complete()
        {
            if (registry is null || !_reported)
            {
                return;
            }

            registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Completed,
                variant.ToString(),
                tag,
                CompletedBytes: null,
                TotalBytes: null,
                StepIndex: stepCount,
                stepCount));
        }

        /// <summary>
        ///     Closes a reported acquisition as failed, with a sanitized reason. Silent when nothing was acquired, so a
        ///     failure in a step this manager never announced (e.g. a rejected request) never surfaces as a runtime
        ///     acquisition failure.
        /// </summary>
        public void Fail(Exception exception)
        {
            if (registry is null || !_reported)
            {
                return;
            }

            var reason = exception is LlamaRuntimeException ? exception.Message : GenericFailureReason;
            registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Failed,
                variant.ToString(),
                tag,
                CompletedBytes: null,
                TotalBytes: null,
                StepIndex: stepCount,
                stepCount,
                reason));
        }
    }
}
