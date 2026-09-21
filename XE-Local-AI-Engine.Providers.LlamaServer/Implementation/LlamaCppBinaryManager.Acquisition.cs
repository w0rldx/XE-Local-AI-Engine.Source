namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Acquisition-visibility branch of <see cref="LlamaCppBinaryManager" />: a per-acquisition reporter that stamps the
///     <c>(variant, tag, step-count)</c> context onto every status write, so the call sites stay one-liners.
/// </summary>
/// <remarks>
///     The manager runs off the startup path while a fully-rendered, idle-looking UI is already on screen, so without
///     this side-channel a slow first-run download is indistinguishable from a broken one.
/// </remarks>
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
    ///     <b>Silence is the default.</b> A cache-hit serve happens on EVERY model spawn, so an acquisition that acquired
    ///     nothing emits nothing: <see cref="Complete" /> and <see cref="Fail" /> are suppressed unless at least one
    ///     non-terminal <see cref="Report" /> already went out, or the hub would carry a <c>Completed</c> per spawn and
    ///     the banner would flicker on a warm cache. <b>Sanitization:</b> <see cref="LlamaRuntimeException" /> messages
    ///     are user-safe by contract; any other exception is collapsed to a generic reason, never surfaced verbatim.
    /// </remarks>
    private sealed class AcquisitionReporter
    {
        /// <summary>The user-safe stand-in for an exception whose message is not sanitized by contract.</summary>
        private const string GenericFailureReason =
            "The llama.cpp runtime could not be downloaded. Check the network connection and try again.";

        private readonly IRuntimeAcquisitionStatusRegistry? _registry;
        private readonly GpuVariant _variant;
        private readonly string? _tag;
        private readonly int _stepCount;

        private bool _reported;

        public AcquisitionReporter(IRuntimeAcquisitionStatusRegistry? registry, GpuVariant variant, string? tag, int stepCount)
        {
            _registry = registry;
            _variant = variant;
            _tag = tag;
            _stepCount = stepCount;
        }

        /// <summary>
        ///     Records one non-terminal status. A no-op when no registry was injected (provider-only / test hosts), which
        ///     keeps those hosts byte-behavior-identical.
        /// </summary>
        public void Report(RuntimeAcquisitionPhase phase, int stepIndex, long? completedBytes = null, long? totalBytes = null)
        {
            if (_registry is null)
            {
                return;
            }

            // Only a real report arms the terminal statuses — see the cache-hit note on the type.
            _reported = true;
            _registry.Report(new RuntimeAcquisitionUpdate
            {
                Phase = phase,
                Variant = _variant.ToString(),
                Tag = _tag,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes,
                StepIndex = stepIndex,
                StepCount = _stepCount
            });
        }

        /// <summary>Closes a reported acquisition as succeeded. Silent when nothing was acquired (cache hit).</summary>
        public void Complete()
        {
            if (_registry is null || !_reported)
            {
                return;
            }

            _registry.Report(new RuntimeAcquisitionUpdate
            {
                Phase = RuntimeAcquisitionPhase.Completed,
                Variant = _variant.ToString(),
                Tag = _tag,
                CompletedBytes = null,
                TotalBytes = null,
                StepIndex = _stepCount,
                StepCount = _stepCount
            });
        }

        /// <summary>
        ///     Closes a reported acquisition as failed, with a sanitized reason. Silent when nothing was acquired, so a
        ///     failure in a step this manager never announced (e.g. a rejected request) never surfaces as a runtime
        ///     acquisition failure.
        /// </summary>
        public void Fail(Exception exception)
        {
            if (_registry is null || !_reported)
            {
                return;
            }

            var reason = exception is LlamaRuntimeException ? exception.Message : GenericFailureReason;
            _registry.Report(new RuntimeAcquisitionUpdate
            {
                Phase = RuntimeAcquisitionPhase.Failed,
                Variant = _variant.ToString(),
                Tag = _tag,
                CompletedBytes = null,
                TotalBytes = null,
                StepIndex = _stepCount,
                StepCount = _stepCount,
                SanitizedError = reason
            });
        }
    }
}
