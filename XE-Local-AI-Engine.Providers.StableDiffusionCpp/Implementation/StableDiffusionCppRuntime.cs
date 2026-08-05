namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     The stable-diffusion.cpp <see cref="IImageRuntime" /> — the orchestration boundary for local image generation.
///     Ensures a resident <c>sd-server</c> daemon via <see cref="IImageServerSupervisor" />, submits the job and polls
///     it via <see cref="SdServerJobClient" />, maps coarse status transitions to <see cref="ImageGenProgress" />, and on
///     completion decodes the base64 image inline (before the 600s result TTL). No sd-server flag, route, or HTTP
///     shape escapes this project.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Cancellation (two-mode).</strong> When <c>ct</c> is signalled the runtime asks sd-server to
///         cancel the job: a still-<em>queued</em> job cancels cleanly (HTTP 200); a job already <em>generating</em>
///         cannot be interrupted (HTTP 409), so the runtime asks the supervisor to tree-kill + restart the daemon,
///         dropping the one active job. The job coordinator invokes both paths simply by cancelling the token it passes to
///         <see cref="GenerateAsync" />.
///     </para>
///     <para>
///         <strong>Fine progress.</strong> The HTTP contract carries only a queue position, so the load / encode /
///         sample / decode timeline is read from the daemon's own stdout via <see cref="IImageServerProgressBroker" />.
///         The subscription taken here is this generation's epoch: it is disposed on EVERY exit path, so a job that was
///         abandoned by a cancel whose cleanup failed can never have its remaining output attributed to the next job.
///     </para>
/// </remarks>
internal sealed class StableDiffusionCppRuntime : IImageRuntime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly SdServerJobClient _jobClient;
    private readonly IImageServerSupervisor _supervisor;
    private readonly IImageServerProgressBroker _progressBroker;

    public StableDiffusionCppRuntime(IImageServerSupervisor supervisor, SdServerJobClient jobClient, IImageServerProgressBroker progressBroker)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _jobClient = jobClient ?? throw new ArgumentNullException(nameof(jobClient));
        _progressBroker = progressBroker ?? throw new ArgumentNullException(nameof(progressBroker));
    }

    /// <inheritdoc />
    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The model name is validated by the supervisor's EnsureRunningAsync.
        var startedTimestamp = Stopwatch.GetTimestamp();
        var tracker = new GenerationProgressTracker(progress, startedTimestamp);

        // Subscribed BEFORE the ensure so a cold model load — minutes for a large file-set — shows as "preparing"
        // rather than as a silent gap. The tracker refuses the step/decode observations until this job's own status
        // says Generating, so only the load phase can be attributed this early.
        using var progressSubscription = _progressBroker.Subscribe(request.ModelName, tracker.ObserveFine);

        var endpoint = await _supervisor.EnsureRunningAsync(request.ModelName, ct).ConfigureAwait(false);

        // Hold an active-job lease for the whole submit→poll→complete window so the idle reaper / LRU evictor never
        // tree-kill this daemon mid-generation, even if the job outruns the idle TTL. A null lease (no live
        // daemon backs the model despite the ensure above — a rare teardown race) proceeds leaseless; the poll loop below
        // then surfaces any failure through the normal error path. Each poll Touch()es the lease to refresh the daemon's
        // idle clock so the idle window is measured from the last observed progress, not from submission.
        using var jobLease = _supervisor.TryAcquireJobLease(request.ModelName);

        string? jobId = null;
        try
        {
            jobId = await _jobClient.SubmitAsync(endpoint.BaseAddress, request, ct).ConfigureAwait(false);
            tracker.ReportCoarse(ImageGenPhase.Queued, queuePosition: null);

            while (true)
            {
                jobLease?.Touch();
                var state = await _jobClient.GetJobAsync(endpoint.BaseAddress, jobId, ct).ConfigureAwait(false);

                // Drives the tracker's attribution gate: only a job the daemon says it is generating may claim the
                // step and decode lines coming off that daemon's stdout.
                tracker.SetGenerating(state.Status == SdJobStatus.Generating);

                switch (state.Status)
                {
                    case SdJobStatus.Completed:
                        tracker.ReportCoarse(ImageGenPhase.Completed, queuePosition: null);
                        return BuildResult(request, state, startedTimestamp);

                    case SdJobStatus.Failed:
                        tracker.ReportCoarse(ImageGenPhase.Failed, queuePosition: null);
                        throw new StableDiffusionRuntimeException("The image runtime failed to generate the image.");

                    case SdJobStatus.Expired:
                        tracker.ReportCoarse(ImageGenPhase.Failed, queuePosition: null);
                        throw new StableDiffusionRuntimeException("The generated image expired before it could be retrieved.");

                    case SdJobStatus.Unknown:
                        tracker.ReportCoarse(ImageGenPhase.Failed, queuePosition: null);
                        throw new StableDiffusionRuntimeException("The image runtime lost track of the generation job.");

                    case SdJobStatus.Cancelled:
                        tracker.ReportCoarse(ImageGenPhase.Cancelled, queuePosition: null);
                        throw new OperationCanceledException("The image generation job was cancelled by the runtime.");

                    case SdJobStatus.Generating:
                        tracker.ReportCoarse(ImageGenPhase.Generating, queuePosition: null);
                        break;

                    case SdJobStatus.Queued:
                        tracker.ReportCoarse(ImageGenPhase.Queued, state.QueuePosition);
                        break;

                    default:
                        tracker.ReportCoarse(ImageGenPhase.Queued, state.QueuePosition);
                        break;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Close the epoch FIRST. The cleanup below may fail to stop the daemon (the cancel POST can throw an
            // HttpRequestException, and the restart can be refused while the spawn gate is busy), in which case the
            // abandoned job keeps printing steps — which must reach nobody, not the next job's tracker.
            progressSubscription.Dispose();

            if (jobId is not null)
            {
                await HandleCancellationAsync(endpoint.BaseAddress, jobId, request.ModelName).ConfigureAwait(false);
            }

            tracker.ReportCoarse(ImageGenPhase.Cancelled, queuePosition: null);
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

        // Report the dimensions of the bytes we actually got back, NOT the requested ones: sd-server rounds the latent
        // grid up to a multiple of 64, so a requested 100x512 arrives as 128x512. Echoing the request here made every
        // consumer (job card, stored image metadata) state a false fact about the produced PNG. The request is only the
        // fallback for a payload whose header cannot be read.
        var produced = PngImageDimensions.TryRead(bytes);

        return new ImageGenerationResult
        {
            ImageBytes = bytes,
            Width = produced?.Width ?? request.Width,
            Height = produced?.Height ?? request.Height,
            Seed = state.Seed ?? request.Seed,
            Format = "png",
            Duration = Stopwatch.GetElapsedTime(startedTimestamp)
        };
    }
}
