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
