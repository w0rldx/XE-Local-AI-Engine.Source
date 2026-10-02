namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     The stable-diffusion.cpp <see cref="IImageRuntime" /> — the orchestration boundary for local image generation.
/// </summary>
/// <remarks>
///     It ensures a resident <c>sd-server</c> daemon via <see cref="IImageServerSupervisor" />, submits and polls the job via
///     <see cref="SdServerJobClient" />, maps coarse status transitions to <see cref="ImageGenProgress" />, and on completion decodes the
///     base64 image inline, before the 600s result TTL. No sd-server flag, route or HTTP shape escapes this project. <strong>Fine
///     progress</strong> comes from the daemon's own stdout via <see cref="IImageServerProgressBroker" />, because the HTTP contract
///     carries only a queue position — see docs/wiki/14-image-generation.md ("Attributing stdout progress to the right generation").
/// </remarks>
internal sealed class StableDiffusionCppRuntime : IImageRuntime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // Bounds the daemon's own error text in the log: it is foreign input of unknown size.
    private const int MaxLoggedErrorLength = 300;

    // The stderr tail is already capped at 4096 characters by ProcessStderrTail; the out-of-memory line sits at its end.
    private const int MaxLoggedStderrLength = 4096;

    private const string OutOfMemoryMessage = "The GPU ran out of memory while loading the image model. Unload other models and retry.";

    private const string FeatureUnsupportedMessage = "The image runtime build does not support this edit mode.";

    private readonly SdServerJobClient _jobClient;
    private readonly IImageServerSupervisor _supervisor;
    private readonly IImageServerProgressBroker _progressBroker;
    private readonly ILogger<StableDiffusionCppRuntime> _logger;

    public StableDiffusionCppRuntime(IImageServerSupervisor supervisor,
        SdServerJobClient jobClient,
        IImageServerProgressBroker progressBroker,
        ILogger<StableDiffusionCppRuntime> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _jobClient = jobClient ?? throw new ArgumentNullException(nameof(jobClient));
        _progressBroker = progressBroker ?? throw new ArgumentNullException(nameof(progressBroker));
    }

    /// <inheritdoc />
    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        SdServerJobClient.ThrowIfEditSourceMissing(request);

        // The model name is validated by the supervisor's EnsureRunningAsync.
        var startedTimestamp = Stopwatch.GetTimestamp();
        var tracker = new GenerationProgressTracker(progress, startedTimestamp);

        // Subscribed BEFORE the ensure so a cold model load — minutes for a large file-set — shows as "preparing" rather than as a silent gap. The tracker refuses the step/decode observations until
        // this job's own status says Generating, so only the load phase can be attributed this early.
        using var progressSubscription = _progressBroker.Subscribe(request.ModelName, tracker.ObserveFine);

        var endpoint = await _supervisor.EnsureRunningAsync(request.ModelName, ct).ConfigureAwait(false);

        // Hold an active-job lease for the whole submit→poll→complete window so the idle reaper / LRU evictor never tree-kill this daemon mid-generation, even if the job outruns the idle TTL. Each
        // poll Touch()es the lease. See docs/wiki/14-image-generation.md ("Daemon leases and the teardown races").
        using var jobLease = _supervisor.TryAcquireJobLease(request.ModelName);

        string? jobId = null;
        try
        {
            if (request.Mode is { } mode)
            {
                await EnsureEditModeSupportedAsync(endpoint.BaseAddress, mode, ct).ConfigureAwait(false);
            }

            jobId = await _jobClient.SubmitAsync(endpoint.BaseAddress, request, ct).ConfigureAwait(false);
            tracker.ReportCoarse(ImageGenPhase.Queued, queuePosition: null);

            while (true)
            {
                jobLease?.Touch();

                // Checked before each poll so a known-dead daemon fails the job now, not after the GET's retry window.
                if (jobLease?.HasDaemonExited(out var exitCode) == true)
                {
                    throw DaemonExited(exitCode, cause: null);
                }

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
                        var outOfMemory = await RecycleAfterFailedJobAsync(request.ModelName, state, jobLease).ConfigureAwait(false);
                        throw outOfMemory
                            ? new StableDiffusionRuntimeException(OutOfMemoryMessage)
                            {
                                OutOfMemory = true
                            }
                            : new StableDiffusionRuntimeException("The image runtime failed to generate the image.");

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
        catch (HttpRequestException ex) when (jobLease?.HasDaemonExited(out var exitCode) == true)
        {
            throw DaemonExited(exitCode, ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Close the epoch FIRST. The cleanup below may fail to stop the daemon (the cancel POST can throw an HttpRequestException, and the restart can be refused while the spawn gate is busy), in
            // which case the abandoned job keeps printing steps — which must reach nobody, not the next job's tracker.
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
    ///     Asks the running daemon once per edit job whether its build accepts the edit input; an older build would
    ///     otherwise ignore the field and silently return a text-to-image result.
    /// </summary>
    private async Task EnsureEditModeSupportedAsync(Uri baseAddress, ImageEditMode mode, CancellationToken ct)
    {
        var capabilities = await _jobClient.GetCapabilitiesAsync(baseAddress, ct).ConfigureAwait(false);
        var supported = mode switch
        {
            ImageEditMode.Img2Img => capabilities.InitImage,
            ImageEditMode.Reference => capabilities.ReferenceImages,
            _ => false
        };

        if (!supported)
        {
            throw new StableDiffusionRuntimeException(FeatureUnsupportedMessage)
            {
                FeatureUnsupported = true
            };
        }
    }

    /// <summary>
    ///     Two-mode cancel: ask sd-server to cancel, and if the job is already generating (409) and so cannot be
    ///     interrupted, tree-kill + restart the daemon to drop it.
    /// </summary>
    /// <remarks>
    ///     Runs on <see cref="CancellationToken.None" />: the caller's token is already cancelled, but the cleanup HTTP
    ///     call and restart must still complete. A still-queued job cancels cleanly with HTTP 200.
    /// </remarks>
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

    /// <summary>
    ///     Logs the daemon's own reason and stderr tail for a failed job and evicts that daemon, so the next generation spawns a fresh
    ///     process instead of reusing one left in a broken state (a CUDA out-of-memory failure keeps failing every job).
    /// </summary>
    /// <returns>Whether the tail shows a GPU out-of-memory; it is read before the eviction, which discards it.</returns>
    /// <remarks>
    ///     The lease is released first: eviction is only safe once no job holds the daemon, and generations are
    ///     serialized by <c>ImageJobCoordinator</c>'s single generation slot, which this call still holds.
    /// </remarks>
    private async Task<bool> RecycleAfterFailedJobAsync(string modelName, SdJobState state, IImageServerJobLease? jobLease)
    {
        var stderrTail = Bound(jobLease?.StderrTail, MaxLoggedStderrLength);
        _logger.LogWarning("sd-server job for model {ModelName} failed ({ErrorCode}): {ErrorMessage}. Last stderr: {StderrTail}. Evicting the daemon; the next generation starts a fresh one.",
            modelName, Bound(state.ErrorCode) ?? "(none)", Bound(state.ErrorMessage) ?? "(none)", stderrTail ?? "(none)");

        jobLease?.Dispose();
        await _supervisor.EvictAsync(modelName, CancellationToken.None).ConfigureAwait(false);
        return stderrTail is not null && IsOutOfMemory(stderrTail);
    }

    // ggml reports an allocation failure as "cudaMalloc failed: out of memory" / "CUDA error: out of memory"; a bare "CUDA error" is any
    // other device fault and must not be relabelled as memory pressure.
    private static bool IsOutOfMemory(string stderrTail) =>
        stderrTail.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || stderrTail.Contains("cudaMalloc failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips control characters (no log forging) and truncates foreign error text to a bounded length.</summary>
    internal static string? Bound(string? text, int maxLength = MaxLoggedErrorLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        foreach (var ch in text)
        {
            if (builder.Length == maxLength)
            {
                return builder.Append('\u2026').ToString();
            }

            _ = builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return builder.ToString().Trim();
    }

    private static StableDiffusionRuntimeException DaemonExited(int? exitCode, Exception? cause)
    {
        var message = exitCode is { } code
            ? string.Create(CultureInfo.InvariantCulture, $"The image server stopped unexpectedly (exit code {code}). It restarts with the next generation.")
            : "The image server stopped unexpectedly. It restarts with the next generation.";
        return cause is null
            ? new StableDiffusionRuntimeException(message)
            {
                ProcessExited = true
            }
            : new StableDiffusionRuntimeException(message, cause)
            {
                ProcessExited = true
            };
    }

    private static ImageGenerationResult BuildResult(ImageGenerationRequest request, SdJobState state, long startedTimestamp)
    {
        if (state.ImageBytes is not { Length: > 0 } bytes)
        {
            throw new StableDiffusionRuntimeException("The image runtime reported completion but returned no image data.");
        }

        // Report the dimensions of the bytes we actually got back, NOT the requested ones: sd-server rounds the latent grid up to a multiple of 64, so a requested 100x512 arrives as 128x512. Echoing
        // the request here made every consumer (job card, stored image metadata) state a false fact about the produced PNG. The request is only the fallback for a payload whose header cannot be read.
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
