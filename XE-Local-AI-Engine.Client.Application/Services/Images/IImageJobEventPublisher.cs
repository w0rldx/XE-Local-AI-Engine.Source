namespace XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Publishes coarse image-job status changes to the connected operator over SignalR. Every event carries the
///     <c>JobId</c> (scopes delivery to the per-job group) and a per-job monotonic <c>Seq</c> (so a late subscriber can
///     replay the buffer and dedupe events delivered both via replay and live). The default implementation is a no-op
///     (<see cref="Implementation.NullImageJobEventPublisher" />); the Client host swaps in a hub-backed publisher
///     (<c>ImageJobEventPublisher</c> over the <c>ImageJobHub</c>). Payloads are coarse status only — never a prompt,
///     path, or step/percent detail (§4A/§10).
/// </summary>
public interface IImageJobEventPublisher
{
    /// <summary>Pushes one coarse status transition for a job to the subscribed operator clients.</summary>
    Task PublishStatusAsync(ImageJobStatusHubEvent statusEvent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Stable SignalR client-method name for image-job status pushes. The React client subscribes to this single method;
///     each push carries the full coarse status, so the client reconciles by job id and dedupes on <c>Seq</c>.
/// </summary>
public static class ImageJobHubEvents
{
    public const string StatusChanged = "imageJob.statusChanged";
}

/// <summary>
///     Coarse image-job status push payload. Carries the job id, the phase name (the
///     <c>ImageJobStatus</c> value: <c>Queued</c>/<c>Generating</c>/<c>Succeeded</c>/<c>Failed</c>/<c>Cancelled</c>), an
///     optional queue position and elapsed time while generating, the produced image id on success, and a sanitized
///     error on failure. <see cref="Seq" /> is the per-job monotonic sequence for replay/dedupe. NO step or percent field
///     (the runtime exposes none over HTTP, §4A). NEVER carries the prompt.
/// </summary>
public sealed record ImageJobStatusHubEvent(
    Guid JobId,
    string Phase,
    int? QueuePosition,
    long? ElapsedMs,
    Guid? ImageId,
    string? SanitizedError,
    long OccurredAtUtc,
    long Seq);

/// <summary>One buffered event in a job's replay log: the SignalR method name and its seq-stamped payload.</summary>
public sealed record ImageJobBufferedEvent(string MethodName, object Payload);
