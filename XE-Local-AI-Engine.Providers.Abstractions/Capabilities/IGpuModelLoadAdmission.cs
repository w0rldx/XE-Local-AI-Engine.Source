namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Process-wide serialization gate for GPU-backed model loads (AUD4-06). Every supervisor that spawns a GPU-backed
///     runtime process — the llama-server supervisor and the stable-diffusion.cpp image supervisor — acquires this gate
///     around the spawn-through-readiness window, so at most ONE GPU load is choosing its placement at a time. Two
///     concurrent <c>--fit</c> loads therefore never read the same free-VRAM snapshot and oversubscribe the device
///     (the last loader spilling weights to system RAM or crashing). CPU-only loads bypass the gate entirely — they do
///     not contend for VRAM. Serialization gives the next waiter a fresh free-VRAM read for free once the current load
///     is resident, which IS the re-evaluation: no byte-level accounting is invented here beyond the existing ledger.
/// </summary>
/// <remarks>
///     <para>
///         The gate is a single shared singleton across supervisors, so an image load and an LLM load serialize against
///         each other. Implementations must be cancellation-safe: a waiter whose token is cancelled abandons the wait
///         cleanly, and a holder always releases via the returned ticket's disposal (the caller wraps it in a
///         <c>using</c>). A bounded max-wait means a waiter never blocks a chat turn forever behind a wedged load — on
///         expiry a <see cref="GpuModelLoadAdmissionTimeoutException" /> is surfaced rather than hanging (the readiness
///         timeouts that bound the holder already make this a rare backstop).
///     </para>
/// </remarks>
public interface IGpuModelLoadAdmission
{
    /// <summary>
    ///     Waits (bounded by the implementation's configured max-wait) for exclusive GPU-load admission and returns a
    ///     ticket that MUST be disposed once the load has become ready or failed — disposal releases the gate for the
    ///     next waiter. Honors <paramref name="ct" /> (a cancelled caller abandons the wait). Throws
    ///     <see cref="GpuModelLoadAdmissionTimeoutException" /> when the bounded wait elapses without admission.
    /// </summary>
    Task<IDisposable> AcquireAsync(CancellationToken ct);
}

/// <summary>
///     No-op <see cref="IGpuModelLoadAdmission" /> floor: admits immediately with no serialization. Wired via
///     <c>TryAddSingleton</c> so a provider-only host (or a test) resolves a gate even when the application layer has not
///     registered the real, metric-emitting serializer. The real gate (registered by the composition root via a plain
///     <c>AddSingleton</c>) wins over this floor.
/// </summary>
public sealed class NoOpGpuModelLoadAdmission : IGpuModelLoadAdmission
{
    /// <inheritdoc />
    public Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IDisposable>(new NoOpTicket());
    }

    private sealed class NoOpTicket : IDisposable
    {
        public void Dispose()
        {
            // Nothing to release — the no-op floor never serialized.
        }
    }
}

/// <summary>
///     Raised when a GPU-load admission wait exceeds the configured max-wait. Surfaced (rather than hanging) so a chat
///     turn behind a wedged model load fails with a clear, user-safe message instead of blocking forever. The message is
///     sanitized — no paths, model identities, or internal detail.
/// </summary>
public sealed class GpuModelLoadAdmissionTimeoutException : Exception
{
    private const string DefaultMessage =
        "The model runtime could not start because another model load did not finish in time. Please try again.";

    /// <summary>Creates the exception with the default sanitized message.</summary>
    public GpuModelLoadAdmissionTimeoutException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates the exception with a sanitized <paramref name="message" />.</summary>
    public GpuModelLoadAdmissionTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a sanitized <paramref name="message" /> and an <paramref name="innerException" />.</summary>
    public GpuModelLoadAdmissionTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
