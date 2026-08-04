namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     All the progress state for ONE image generation: what has been reported, what the daemon's stdout has revealed,
///     and whether an out-of-band observation may currently be believed. Owned by a single
///     <see cref="StableDiffusionCppRuntime.GenerateAsync" /> call and thrown away with it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why an object and not a few locals.</b> The dedupe state used to be a <c>ref ImageGenPhase?</c> threaded
///         through a static reporter. A <c>ref</c> local cannot be captured by a lambda (CS8175), and the fine phases
///         arrive on the launcher's stdout drain thread through a callback — so the state had to move onto an instance
///         before any of this was expressible at all.
///     </para>
///     <para>
///         <b>Attribution.</b> Fine observations are read from a daemon's stdout, which says nothing about which job
///         produced them. They are believed only under the rules in <see cref="ObserveFine" />, keyed on this
///         generation's own polled HTTP status — never on "some job is active". The coordinator releases its generation
///         slot in a <c>finally</c> even when the cancel path throws something other than a
///         <c>StableDiffusionRuntimeException</c> (an <c>HttpRequestException</c> out of the cancel POST, or a restart
///         refused because the spawn gate is busy), so the daemon can still be working on an abandoned job while the
///         next one starts. The subscription handle that feeds this tracker is disposed on every exit path, which is
///         what keeps an abandoned generation's output from ever reaching the next job's tracker.
///     </para>
///     <para>Thread-safe: the poll loop and the stdout drain thread both report through it.</para>
/// </remarks>
internal sealed class GenerationProgressTracker
{
    private readonly IProgress<ImageGenProgress>? _progress;
    private readonly long _startedTimestamp;
    private readonly Lock _gate = new();

    private ImageGenPhase? _lastPhase;
    private int? _lastQueuePosition;
    private int? _step;
    private int? _totalSteps;
    private double? _secondsPerIteration;

    // Latched once a sampler step has been seen: after that, a "loading"/"encoding" line is the decode-side weight
    // load, not a rewind to the start of the job.
    private bool _samplingSeen;

    // This generation's own HTTP status is Generating — the precondition for believing a step or decode observation.
    private bool _isGenerating;

    // Latched at the first terminal report so nothing can be pushed after the job is done.
    private bool _isTerminal;

    public GenerationProgressTracker(IProgress<ImageGenProgress>? progress, long startedTimestamp)
    {
        _progress = progress;
        _startedTimestamp = startedTimestamp;
    }

    /// <summary>
    ///     Records whether this job's polled status is currently <c>Generating</c>. sd-server runs one generation at a
    ///     time, so a job that has reached Generating owns the daemon's output until it leaves that state.
    /// </summary>
    public void SetGenerating(bool isGenerating)
    {
        lock (_gate)
        {
            _isGenerating = isGenerating;
        }
    }

    /// <summary>
    ///     Reports a coarse HTTP-status transition. Repeat observations of the same phase are dropped, except while
    ///     queued (where a changed queue position is news). A coarse <c>Generating</c> never overwrites a fine phase:
    ///     doing so would flicker the card between "sampling 5/20" and a bare "generating" on every poll.
    /// </summary>
    public void ReportCoarse(ImageGenPhase phase, int? queuePosition)
    {
        lock (_gate)
        {
            if (_isTerminal)
            {
                return;
            }

            if (IsTerminalPhase(phase))
            {
                _isTerminal = true;
            }
            else if (phase == ImageGenPhase.Generating && IsFinePhase(_lastPhase))
            {
                return;
            }
            else if (_lastPhase == phase && (phase != ImageGenPhase.Queued || _lastQueuePosition == queuePosition))
            {
                return;
            }

            if (phase != ImageGenPhase.Sampling)
            {
                ClearSamplingCounters();
            }

            _lastPhase = phase;
            _lastQueuePosition = queuePosition;
            Push(phase, queuePosition);
        }
    }

    /// <summary>
    ///     Folds in one observation parsed from the daemon's stdout, dropping the ones that cannot honestly be
    ///     attributed to this generation or that would move the bar backwards.
    /// </summary>
    public void ObserveFine(SdProgressObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        lock (_gate)
        {
            if (_isTerminal || !ShouldBelieve(observation))
            {
                return;
            }

            if (observation.Phase == ImageGenPhase.Sampling)
            {
                _step = observation.Step;
                _totalSteps = observation.TotalSteps;
                _secondsPerIteration = observation.SecondsPerIteration;
                _samplingSeen = true;
            }
            else if (observation.Phase != ImageGenPhase.Decoding)
            {
                // Decoding keeps the step counters so the bar stays full while the image is written out; loading and
                // encoding precede sampling entirely, so any counters they inherit would be from another generation.
                ClearSamplingCounters();
            }

            _lastPhase = observation.Phase;
            _lastQueuePosition = null;
            Push(observation.Phase, queuePosition: null);
        }
    }

    /// <summary>The attribution + monotonicity rules. Caller holds the lock.</summary>
    private bool ShouldBelieve(SdProgressObservation observation)
    {
        // A step count or a decode belongs to whichever job the daemon is actually running. Only a job whose own
        // status says Generating can make that claim.
        if (observation.Phase is ImageGenPhase.Sampling or ImageGenPhase.Decoding && !_isGenerating)
        {
            return false;
        }

        // Weight loads reappear after sampling (the VAE is prepared lazily). Once sampling has started, "loading"
        // means the job is finishing, not restarting — reporting it would rewind the UI to "preparing model".
        if (observation.Phase is ImageGenPhase.Loading or ImageGenPhase.Encoding && _samplingSeen)
        {
            return false;
        }

        // Nothing new to say — but a repeated step IS news if its counters moved.
        if (observation.Phase != ImageGenPhase.Sampling)
        {
            return _lastPhase != observation.Phase;
        }

        // A step that is not ahead of the last one is a stale frame (the daemon may still be flushing an abandoned
        // job's output). Believing it would let the bar jump backwards.
        return observation.Step is { } step && (_step is not { } lastStep || step > lastStep || _totalSteps != observation.TotalSteps);
    }

    private void ClearSamplingCounters()
    {
        _step = null;
        _totalSteps = null;
        _secondsPerIteration = null;
    }

    /// <summary>Builds and pushes one observation. Caller holds the lock, so pushes are ordered and never interleave.</summary>
    private void Push(ImageGenPhase phase, int? queuePosition)
    {
        _progress?.Report(new ImageGenProgress
        {
            Phase = phase,
            QueuePosition = queuePosition,
            Elapsed = Stopwatch.GetElapsedTime(_startedTimestamp),
            Step = _step,
            TotalSteps = _totalSteps,
            SecondsPerIteration = _secondsPerIteration,
            EstimatedRemaining = EstimateRemaining(phase)
        });
    }

    /// <summary>
    ///     The estimate, and the deliberate refusal to produce one outside sampling. Loading and encoding have no
    ///     measurable rate, and the decode that follows the last step has no step counter at all — a countdown that
    ///     survived into either would show "0s left" while the job kept running, which is the exact complaint this
    ///     phase-aware timeline exists to fix.
    /// </summary>
    private TimeSpan? EstimateRemaining(ImageGenPhase phase)
    {
        if (phase != ImageGenPhase.Sampling
            || _step is not { } step
            || _totalSteps is not { } total
            || _secondsPerIteration is not { } secondsPerIteration
            || secondsPerIteration <= 0
            || step >= total)
        {
            return null;
        }

        return TimeSpan.FromSeconds((total - step) * secondsPerIteration);
    }

    private static bool IsFinePhase(ImageGenPhase? phase)
    {
        return phase is ImageGenPhase.Loading or ImageGenPhase.Encoding or ImageGenPhase.Sampling or ImageGenPhase.Decoding;
    }

    private static bool IsTerminalPhase(ImageGenPhase phase)
    {
        return phase is ImageGenPhase.Completed or ImageGenPhase.Failed or ImageGenPhase.Cancelled;
    }
}
