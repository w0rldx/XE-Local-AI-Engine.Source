namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Every knob the external-integration surface has. The compiled defaults are the shipped posture, so there is no
///     <c>appsettings.json</c> entry and an empty section binds cleanly. Bound from <see cref="Section" /> and validated
///     on startup with <c>ValidateDataAnnotations().ValidateOnStart()</c>, which also runs the
///     <see cref="IValidatableObject" /> member below.
/// </summary>
/// <remarks>
///     Several members are owned here and consumed elsewhere — this class is where each number has one home and one
///     bound, and each doc comment says who reads it.
/// </remarks>
public sealed class IntegrationOptions : IValidatableObject
{
    public const string Section = "Integrations";

    /// <summary>
    ///     The worst-case bytes a serialized <see cref="IntegrationStreamEvent" /> adds around an
    ///     <c>external.output</c> payload envelope: the <c>type</c>, <c>sequence</c>, two GUIDs, <c>occurredAtUtc</c>
    ///     and their property names. <see cref="MaxOutputBytes" /> bounds only the persisted
    ///     <c>{"contentType": …, "payload": …}</c> envelope, but the replay ring measures the whole stream event, so
    ///     comparing the two directly accepted an output that could never fit in the ring: it landed, the ring trimmed
    ///     it away immediately, and a caller already streaming a 200 got the close without its committed output.
    ///     <para>
    ///         Pinned rather than computed, so a bound is a constant an operator can reason about.
    ///         <c>IntegrationOptionsEnvelopeOverheadTests</c> measures a maximal event and fails if the real overhead
    ///         ever grows past this; the headroom over the measured ~200 bytes is the margin for a longer event type.
    ///     </para>
    /// </summary>
    public const int MaxStreamEventEnvelopeBytes = 512;

    /// <summary>The narrowest replay window allowed: below this a caller could not realistically re-attach after a drop.</summary>
    private static readonly TimeSpan MinEventBufferTtl = TimeSpan.FromSeconds(10);

    /// <summary>The widest replay window allowed; beyond a day the buffer is a store, and the store is the events table.</summary>
    private static readonly TimeSpan MaxEventBufferTtl = TimeSpan.FromHours(24);

    /// <summary>
    ///     The node-wide cap on executions in <c>Accepted</c>, <c>Queued</c> or <c>Running</c>. Enforced inside the
    ///     admission transaction; exceeding it answers 503 with a <c>Retry-After</c> and writes nothing.
    /// </summary>
    [Range(1, 1024)]
    public int MaxQueuedExecutions { get; init; } = 8;

    /// <summary>
    ///     The per-principal companion to <see cref="MaxQueuedExecutions" />, enforced in the same transaction. The
    ///     default of 2 is deliberately far below the node-wide 8: it is a fairness floor, not a throughput setting, and
    ///     its whole purpose is that one noisy integrator cannot fill the node's queue and starve every other principal
    ///     and the interactive user.
    /// </summary>
    [Range(1, 1024)]
    public int MaxQueuedExecutionsPerPrincipal { get; init; } = 2;

    /// <summary>
    ///     How long a still-queued execution may wait for the invocation lease before it is failed with
    ///     <c>queue-timeout</c>. Consumed by the execution coordinator. A caller learns its request will not run instead
    ///     of waiting behind a long generation forever.
    /// </summary>
    [Range(1, 86_400)]
    public int MaxQueueAgeSeconds { get; init; } = 120;

    /// <summary>
    ///     The invoke body ceiling in bytes. <b>This property is where the number lives, not where it is enforced:</b>
    ///     the limit is applied while the route is being built, before any <c>IOptions&lt;T&gt;</c> can be resolved, so
    ///     the endpoint composition reads it from configuration the way the rate-limit constants do. Keep it here so the
    ///     value has one home and one bound.
    /// </summary>
    [Range(1024, 16 * 1024 * 1024)]
    public int MaxRequestBodyBytes { get; init; } = 1_048_576;

    /// <summary>The ceiling on the composed seed turn, after the inputs are concatenated and framed.</summary>
    [Range(1024, 4 * 1024 * 1024)]
    public int MaxSeedBytes { get; init; } = 262_144;

    /// <summary>How many events one execution's replay buffer retains before it drops the oldest.</summary>
    [Range(16, 65_536)]
    public int EventBufferCapacity { get; init; } = 2048;

    /// <summary>The byte ceiling on one execution's replay buffer, whichever bound bites first.</summary>
    [Range(64 * 1024, 64 * 1024 * 1024)]
    public int EventBufferMaxBytes { get; init; } = 4_194_304;

    /// <summary>How many executions may hold a replay buffer at once. Terminal ones are evicted first; beyond that a new attach is refused.</summary>
    [Range(1, 1024)]
    public int MaxTrackedExecutions { get; init; } = 64;

    /// <summary>
    ///     How long a terminal execution's replay buffer survives so a dropped caller can re-attach. Bounded in
    ///     <see cref="Validate" /> rather than by an attribute, because <c>RangeAttribute</c> is numeric and a
    ///     <see cref="TimeSpan" /> has no numeric bound for it.
    /// </summary>
    public TimeSpan EventBufferTtlAfterTerminal { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The ceiling on one <c>emit_output</c> call's payload, in plaintext UTF-8 bytes.</summary>
    [Range(1024, 4 * 1024 * 1024)]
    public int MaxOutputBytes { get; init; } = 262_144;

    /// <summary>
    ///     The aggregate ceiling on one execution's <c>external.output</c> payloads, in <b>plaintext</b> UTF-8 bytes.
    ///     The running total lives in <c>integration_executions.output_bytes</c>; there is no ciphertext
    ///     <c>length()</c> anywhere in this feature.
    /// </summary>
    [Range(1024, 64 * 1024 * 1024)]
    public int MaxOutputBytesPerExecution { get; init; } = 1_048_576;

    /// <summary>
    ///     The <b>per-principal</b> request ceiling, enforced inside the hand-mapped handlers after authentication,
    ///     where a principal exists to partition on. Not to be confused with <see cref="IpRateLimitPerMinute" />.
    /// </summary>
    [Range(1, 100_000)]
    public int RateLimitPerMinute { get; init; } = 600;

    /// <summary>
    ///     The <b>route-level</b> ceiling, which runs before authentication and partitions by remote IP — so on loopback
    ///     every caller shares one bucket. It is a coarse abuse ceiling and nothing else, which is why its default is ten
    ///     times <see cref="RateLimitPerMinute" />.
    /// </summary>
    [Range(1, 1_000_000)]
    public int IpRateLimitPerMinute { get; init; } = 6_000;

    /// <summary>
    ///     The compaction bound for integration turns, so they never read the work-session budget. Consumed by the
    ///     session context builder; nothing wires it up yet.
    /// </summary>
    [Range(1, 1_000_000)]
    public int ContextBudgetTokens { get; init; } = 12_000;

    /// <summary>
    ///     The byte budget for the framed "prior outputs" document a caller-managed continuation carries, so the model
    ///     can see what it already emitted. A byte budget rather than a token budget, which is why it does not fold into
    ///     <see cref="ContextBudgetTokens" />. Consumed by the session context builder.
    /// </summary>
    [Range(1024, 1024 * 1024)]
    public int PriorOutputsContextBytes { get; init; } = 32_768;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EventBufferTtlAfterTerminal < MinEventBufferTtl || EventBufferTtlAfterTerminal > MaxEventBufferTtl)
        {
            yield return new ValidationResult(
                $"{Section}:{nameof(EventBufferTtlAfterTerminal)} must be between {MinEventBufferTtl} and {MaxEventBufferTtl}.",
                [nameof(EventBufferTtlAfterTerminal)]);
        }

        // A per-principal cap above the node-wide one is not a tighter fairness floor, it is a dead number: the
        // node-wide count is checked first and always bites first.
        if (MaxQueuedExecutionsPerPrincipal > MaxQueuedExecutions)
        {
            yield return new ValidationResult(
                $"{Section}:{nameof(MaxQueuedExecutionsPerPrincipal)} must not exceed {nameof(MaxQueuedExecutions)}.",
                [nameof(MaxQueuedExecutionsPerPrincipal), nameof(MaxQueuedExecutions)]);
        }

        // One output event larger than the whole ring's byte cap trims the ring to empty the moment it lands, which
        // costs every event before it and hands the reader a gap for a run that is still producing. The comparison is
        // against the payload envelope PLUS the stream event around it, because that is what the ring measures.
        if (EventBufferMaxBytes < (long)MaxOutputBytes + MaxStreamEventEnvelopeBytes)
        {
            yield return new ValidationResult(
                $"{Section}:{nameof(EventBufferMaxBytes)} must be at least {nameof(MaxOutputBytes)} plus {MaxStreamEventEnvelopeBytes} bytes of stream-event envelope.",
                [nameof(EventBufferMaxBytes), nameof(MaxOutputBytes)]);
        }

        // A single emit_output larger than the whole execution's budget can never be accepted, so the per-call ceiling
        // would silently be the aggregate one.
        if (MaxOutputBytes > MaxOutputBytesPerExecution)
        {
            yield return new ValidationResult(
                $"{Section}:{nameof(MaxOutputBytes)} must not exceed {nameof(MaxOutputBytesPerExecution)}.",
                [nameof(MaxOutputBytes), nameof(MaxOutputBytesPerExecution)]);
        }
    }
}
