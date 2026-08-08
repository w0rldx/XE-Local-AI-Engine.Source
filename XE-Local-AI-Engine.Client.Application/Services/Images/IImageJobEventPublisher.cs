namespace XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Publishes image-job status changes to the connected operator over SignalR. Every event carries the
///     <c>JobId</c> (scopes delivery to the per-job group) and a per-job monotonic <c>Seq</c> (so a late subscriber can
///     replay the buffer and dedupe events delivered both via replay and live). The default implementation is a no-op
///     (<see cref="Implementation.NullImageJobEventPublisher" />); the Client host swaps in a hub-backed publisher
///     (<c>ImageJobEventPublisher</c> over the <c>ImageJobHub</c>). Payloads carry status and progress counters only —
///     never a prompt or a path.
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
///     Image-job status push payload. Carries the job id, the phase name (the
///     <c>ImageJobStatus</c> value: <c>Queued</c>/<c>Generating</c>/<c>Succeeded</c>/<c>Failed</c>/<c>Cancelled</c>), an
///     optional queue position and elapsed time while generating, the produced image id on success, and a sanitized
///     error on failure. <see cref="Seq" /> is the per-job monotonic sequence for replay/dedupe. NEVER carries the prompt.
///     <para>
///         The generation-timeline fields are init-only additions rather than more positional parameters: this record is
///         constructed and projected in several places, and a widening positional list moves every one of them for no
///         gain. They are all nullable because the runtime only observes them for part of a job — an absent value means
///         "not known here", and the client must render the coarse phase alone rather than substituting a zero.
///     </para>
/// </summary>
public sealed record ImageJobStatusHubEvent(
    Guid JobId,
    string Phase,
    int? QueuePosition,
    long? ElapsedMs,
    Guid? ImageId,
    string? SanitizedError,
    long OccurredAtUtc,
    long Seq)
{
    /// <summary>
    ///     The fine phase within <c>Generating</c> — <c>Loading</c>/<c>Encoding</c>/<c>Sampling</c>/<c>Decoding</c> —
    ///     or <see langword="null" /> when the runtime cannot see inside the generation. The client keys its
    ///     "preparing" / step-bar / "finishing" copy on this, which is what keeps a countdown off the screen during the
    ///     phases that have no measurable rate.
    /// </summary>
    public string? GenerationPhase { get; init; }

    /// <summary>Completed sampling steps, set only while sampling (and held through decode so the bar can stay full).</summary>
    public int? Step { get; init; }

    /// <summary>Total sampling steps for this job, pairing with <see cref="Step" />.</summary>
    public int? TotalSteps { get; init; }

    /// <summary>Measured seconds per sampling iteration — the observed rate behind <see cref="EstimatedRemainingMs" />.</summary>
    public double? SecondsPerIteration { get; init; }

    /// <summary>
    ///     Estimated milliseconds left in the SAMPLING phase, or <see langword="null" /> whenever no honest estimate
    ///     exists. Never zero-as-placeholder: a countdown that reaches zero and then keeps running is the failure this
    ///     field's nullability exists to prevent.
    /// </summary>
    public long? EstimatedRemainingMs { get; init; }
}

/// <summary>One buffered event in a job's replay log: the SignalR method name and its seq-stamped payload.</summary>
public sealed record ImageJobBufferedEvent(string MethodName, object Payload);
