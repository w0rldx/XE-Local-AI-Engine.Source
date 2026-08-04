namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Carries the fine generation progress that sd-server prints on its own stdout — and only that — from the process
///     launcher (the only component holding the child's streams) to the runtime facade (the only component that knows
///     which generation is in flight).
///     <para>
///         <b>Privacy.</b> The launcher parses each drained line and publishes a <see cref="SdProgressObservation" />;
///         the raw line NEVER crosses this seam. That is load-bearing rather than tidy: sd-server echoes the request
///         prompt in its own debug output (<c>parse '&lt;prompt&gt;' to [...]</c>), which is why the stdout forward to the
///         app log is pinned at Debug. A broker that carried lines would route prompts into the progress path and out
///         over the status hub.
///     </para>
///     <para>
///         Delivery is best-effort and fire-and-forget: a model with no subscriber drops its observations, and a handler
///         that throws must never take down the drain loop that feeds the child's stdout pipe.
///     </para>
/// </summary>
internal interface IImageServerProgressBroker
{
    /// <summary>Publishes one parsed observation for <paramref name="modelName" /> to whatever subscriber is listening.</summary>
    void Publish(string modelName, SdProgressObservation observation);

    /// <summary>
    ///     Subscribes <paramref name="handler" /> to <paramref name="modelName" />'s observations until the returned
    ///     handle is disposed. The handle is the generation epoch: the runtime takes one per generation and disposes it
    ///     on EVERY exit path, so an abandoned generation's continuing output can never be attributed to the next job.
    /// </summary>
    IDisposable Subscribe(string modelName, Action<SdProgressObservation> handler);
}

/// <summary>
///     One parsed fine-progress observation: the phase, plus the sampling step counters when the observed line was a
///     sampler step. Deliberately carries no text — see <see cref="IImageServerProgressBroker" /> on prompt privacy.
/// </summary>
/// <param name="Phase">The fine phase the observed line implies.</param>
/// <param name="Step">Completed sampling steps, or <see langword="null" /> outside a sampler step line.</param>
/// <param name="TotalSteps">Total sampling steps, or <see langword="null" /> outside a sampler step line.</param>
/// <param name="SecondsPerIteration">
///     Measured seconds per sampling iteration. sd-server prints the rate as <c>s/it</c> below 1 it/s and as
///     <c>it/s</c> above it; both are normalized to seconds here so the consumer never has to know which it read.
/// </param>
internal sealed record SdProgressObservation(ImageGenPhase Phase, int? Step = null, int? TotalSteps = null, double? SecondsPerIteration = null);
