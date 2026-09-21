namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Process-wide serialization gate for GPU-backed model loads: the llama-server and stable-diffusion.cpp image
///     supervisors hold it across the spawn-through-readiness window, so at most ONE GPU load chooses placement at a
///     time.
/// </summary>
/// <remarks>
///     Two concurrent <c>--fit</c> loads therefore never read the same free-VRAM snapshot and oversubscribe the device
///     (the last loader spilling weights to system RAM or crashing). Serialization hands the next waiter a fresh
///     free-VRAM read once the current load is resident, which IS the re-evaluation: no byte-level accounting is
///     invented beyond the existing ledger. CPU-only loads bypass the gate entirely — they do not contend for VRAM.
///     One shared singleton serves every supervisor, so an image load and an LLM load serialize against each other.
/// </remarks>
public interface IGpuModelLoadAdmission
{
    /// <summary>
    ///     Waits (bounded by the implementation's configured max-wait) for exclusive GPU-load admission and returns a
    ///     ticket whose disposal releases the gate for the next waiter.
    /// </summary>
    /// <remarks>
    ///     The caller wraps the ticket in a <c>using</c> and disposes it once the load has become ready or failed.
    ///     Implementations must be cancellation-safe: a waiter whose <paramref name="ct" /> is cancelled abandons the
    ///     wait cleanly. On expiry of the bounded wait a <see cref="GpuModelLoadAdmissionTimeoutException" /> is thrown
    ///     rather than blocking a chat turn forever behind a wedged load; the readiness timeouts that bound the holder
    ///     already make that a rare backstop.
    /// </remarks>
    Task<IDisposable> AcquireAsync(CancellationToken ct);
}

/// <summary>No-op <see cref="IGpuModelLoadAdmission" /> floor: admits immediately with no serialization.</summary>
/// <remarks>
///     Wired via <c>TryAddSingleton</c> so a provider-only host (or a test) resolves a gate even when the application
///     layer has not registered the real, metric-emitting serializer. That real gate, registered by the composition
///     root via a plain <c>AddSingleton</c>, wins over this floor.
/// </remarks>
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
        }
    }
}

/// <summary>Raised when a GPU-load admission wait exceeds the configured max-wait.</summary>
/// <remarks>
///     Surfaced rather than hanging, so a chat turn behind a wedged model load fails with a clear, user-safe message
///     instead of blocking forever. The message is sanitized — no paths, model identities, or internal detail.
/// </remarks>
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
