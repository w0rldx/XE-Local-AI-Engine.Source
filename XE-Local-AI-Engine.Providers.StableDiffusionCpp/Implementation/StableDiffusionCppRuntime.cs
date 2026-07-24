namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     The stable-diffusion.cpp <see cref="IImageRuntime" /> — the orchestration boundary for local image generation.
///     Ensures a resident <c>sd-server</c> daemon via <see cref="IImageServerSupervisor" />, submits the job and polls
///     it via <see cref="SdServerJobClient" />, maps coarse status transitions to <see cref="ImageGenProgress" />, and on
///     completion decodes the base64 image inline (before the 600s result TTL, §4A). No sd-server flag, route, or HTTP
///     shape escapes this project (architecture invariant §3).
/// </summary>
/// <remarks>
///     <para>
///         <strong>Cancellation (two-mode, §4A).</strong> When <c>ct</c> is signalled the runtime asks sd-server to
///         cancel the job: a still-<em>queued</em> job cancels cleanly (HTTP 200); a job already <em>generating</em>
///         cannot be interrupted (HTTP 409), so the runtime asks the supervisor to tree-kill + restart the daemon,
///         dropping the one active job. Lane C invokes both paths simply by cancelling the token it passes to
///         <see cref="GenerateAsync" />.
///     </para>
/// </remarks>
internal sealed class StableDiffusionCppRuntime : IImageRuntime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly SdServerJobClient _jobClient;
    private readonly IImageServerSupervisor _supervisor;

    public StableDiffusionCppRuntime(IImageServerSupervisor supervisor, SdServerJobClient jobClient)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _jobClient = jobClient ?? throw new ArgumentNullException(nameof(jobClient));
    }

    /// <inheritdoc />
    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The model name is validated by the supervisor's EnsureRunningAsync.
        var startedTimestamp = Stopwatch.GetTimestamp();
        var endpoint = await _supervisor.EnsureRunningAsync(request.ModelName, ct).ConfigureAwait(false);

        // Hold an active-job lease for the whole submit→poll→complete window so the idle reaper / LRU evictor never
        // tree-kill this daemon mid-generation, even if the job outruns the idle TTL. A null lease (no live
        // daemon backs the model despite the ensure above — a rare teardown race) proceeds leaseless; the poll loop below
        // then surfaces any failure through the normal error path. Each poll Touch()es the lease to refresh the daemon's
        // idle clock so the idle window is measured from the last observed progress, not from submission.
        using var jobLease = _supervisor.TryAcquireJobLease(request.ModelName);

        string? jobId = null;
        var lastPhase = (ImageGenPhase?)null;
        try
        {
            jobId = await _jobClient.SubmitAsync(endpoint.BaseAddress, request, ct).ConfigureAwait(false);
            Report(progress, ref lastPhase, ImageGenPhase.Queued, queuePosition: null, startedTimestamp);

            while (true)
            {
                jobLease?.Touch();
                var state = await _jobClient.GetJobAsync(endpoint.BaseAddress, jobId, ct).ConfigureAwait(false);
                switch (state.Status)
                {
                    case SdJobStatus.Completed:
                        Report(progress, ref lastPhase, ImageGenPhase.Completed, queuePosition: null, startedTimestamp);
                        return BuildResult(request, state, startedTimestamp);

                    case SdJobStatus.Failed:
                        Report(progress, ref lastPhase, ImageGenPhase.Failed, queuePosition: null, startedTimestamp);
                        throw new StableDiffusionRuntimeException("The image runtime failed to generate the image.");

                    case SdJobStatus.Expired:
                        Report(progress, ref lastPhase, ImageGenPhase.Failed, queuePosition: null, startedTimestamp);
                        throw new StableDiffusionRuntimeException("The generated image expired before it could be retrieved.");

                    case SdJobStatus.Unknown:
                        Report(progress, ref lastPhase, ImageGenPhase.Failed, queuePosition: null, startedTimestamp);
                        throw new StableDiffusionRuntimeException("The image runtime lost track of the generation job.");

                    case SdJobStatus.Cancelled:
                        Report(progress, ref lastPhase, ImageGenPhase.Cancelled, queuePosition: null, startedTimestamp);
                        throw new OperationCanceledException("The image generation job was cancelled by the runtime.");

                    case SdJobStatus.Generating:
                        Report(progress, ref lastPhase, ImageGenPhase.Generating, queuePosition: null, startedTimestamp);
                        break;

                    case SdJobStatus.Queued:
                        Report(progress, ref lastPhase, ImageGenPhase.Queued, state.QueuePosition, startedTimestamp);
                        break;

                    default:
                        Report(progress, ref lastPhase, ImageGenPhase.Queued, state.QueuePosition, startedTimestamp);
                        break;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (jobId is not null)
            {
                await HandleCancellationAsync(endpoint.BaseAddress, jobId, request.ModelName).ConfigureAwait(false);
            }

            Report(progress, ref lastPhase, ImageGenPhase.Cancelled, queuePosition: null, startedTimestamp);
            throw;
        }
    }

    /// <summary>
    ///     Two-mode cancel: ask sd-server to cancel; if the job is already generating (409) it cannot be interrupted, so
    ///     tree-kill + restart the daemon to drop it. Runs on <see cref="CancellationToken.None" /> — the caller's token
    ///     is already cancelled, but the cleanup HTTP call / restart must still complete.
    /// </summary>
    private async Task HandleCancellationAsync(Uri baseAddress, string jobId, string modelName)
    {
        SdCancelOutcome outcome;
        try
        {
            outcome = await _jobClient.CancelAsync(baseAddress, jobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StableDiffusionRuntimeException)
        {
            // Best-effort cleanup on the cancellation path — a failed cancel POST must not mask the OperationCanceled.
            return;
        }

        if (outcome == SdCancelOutcome.Generating)
        {
            await _supervisor.RestartAsync(modelName, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ImageGenerationResult BuildResult(ImageGenerationRequest request, SdJobState state, long startedTimestamp)
    {
        if (state.ImageBytes is not { Length: > 0 } bytes)
        {
            throw new StableDiffusionRuntimeException("The image runtime reported completion but returned no image data.");
        }

        return new ImageGenerationResult
        {
            ImageBytes = bytes,
            Width = request.Width,
            Height = request.Height,
            Seed = state.Seed ?? request.Seed,
            Format = "png",
            Duration = Stopwatch.GetElapsedTime(startedTimestamp)
        };
    }

    /// <summary>Reports a coarse phase transition, de-duplicating repeat observations of the same phase+position.</summary>
    private static void Report(IProgress<ImageGenProgress>? progress,
        ref ImageGenPhase? lastPhase,
        ImageGenPhase phase,
        int? queuePosition,
        long startedTimestamp)
    {
        if (progress is null)
        {
            lastPhase = phase;
            return;
        }

        // Report the first observation of each phase; for the queued phase also report a changed queue position.
        if (lastPhase == phase && phase != ImageGenPhase.Queued)
        {
            return;
        }

        lastPhase = phase;
        progress.Report(new ImageGenProgress
        {
            Phase = phase,
            QueuePosition = queuePosition,
            Elapsed = Stopwatch.GetElapsedTime(startedTimestamp)
        });
    }
}
