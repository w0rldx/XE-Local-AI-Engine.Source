namespace XE_Local_AI_Engine.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>Terminal state of one llama-server spawn-through-readiness attempt.</summary>
public enum LlamaServerReadinessOutcome
{
    Ready,
    Failed,
    Cancelled
}

/// <summary>Measured placement class for one llama-server load attempt.</summary>
public enum LlamaServerPlacementOutcome
{
    /// <summary>A CPU build: there was no placement question to ask.</summary>
    Cpu,

    /// <summary>Every layer landed on the GPU.</summary>
    Full,

    /// <summary>Some layers landed on the GPU and the rest run from system RAM.</summary>
    Partial,

    /// <summary>No banner was observed, so placement was never measured.</summary>
    Unknown,

    /// <summary>
    ///     A GPU build placed NONE of the model's layers on the GPU (<c>0/N</c>) — it is serving entirely from system
    ///     RAM. Distinguished from <see cref="Partial" /> because the two say different things about a measurement, and
    ///     appended last so the existing ordinals are unchanged.
    /// </summary>
    None
}

/// <summary>Whether this was the primary launch candidate or the explicit one-shot KV/FA-safe retry.</summary>
public enum LlamaServerLoadAttemptKind
{
    Primary,
    SafeRetry
}

/// <summary>
///     Observation of one llama-server load attempt. It is report-only: consumers must not use it as a memory ledger or
///     an admission decision — the two VRAM figures below are a RECORD of what admission already decided, never an input
///     to a later one.
/// </summary>
/// <remarks>
///     <para>
///         Every member except <see cref="ModelName" /> is content-free and bounded-cardinality. The model name is
///         carried so a host-side consumer can key a per-model record; it must NOT reach a metric tag, where it would
///         be unbounded cardinality (the meter bridge deliberately tags role/variant/outcome only).
///     </para>
/// </remarks>
/// <param name="ModelName">
///     The model this load was for. Carried for keying only; never a metric tag.
/// </param>
/// <param name="GlobalFreeVramBytesAtLoad">
///     Machine-global free VRAM as the capacity gate measured it immediately before admitting THIS load — its forced
///     hardware re-probe under the decision gate, carried here rather than re-measured (see
///     <c>ProcessLaunchAdmission.GlobalFreeVramBytesAtAdmission</c>). Null when the load carried no capacity admission
///     (a direct, profiling or test spawn), when the box has no readable global-free figure (a non-NVIDIA or CPU-only
///     host), or when the selected runtime variant moved off the one the admission was granted against.
/// </param>
/// <param name="AdmittedVramBytes">
///     The GPU bytes the capacity gate RESERVED for this process — the admitted allocation's footprint, NOT llama.cpp's
///     own <c>--list-devices</c> process budget (a different axis, and not read on this path). Zero is a real answer for
///     a CPU-placed allocation; null means there was no admission to read.
/// </param>
public sealed record LlamaServerLoadObservation(
    ModelRole Role,
    GpuVariant Variant,
    string RuntimeVersion,
    string? RuntimeSha256,
    double ReadinessDurationMs,
    LlamaServerReadinessOutcome Outcome,
    LlamaServerPlacementOutcome Placement,
    LlamaServerLoadAttemptKind AttemptKind,
    SpeculativeModeClass SpeculativeModeClass,
    // Required: every construction site must supply the model name. Only the two long? members below are
    // trailing-optional, so a caller that measured nothing keeps constructing as it always did.
    string ModelName,
    long? GlobalFreeVramBytesAtLoad = null,
    long? AdmittedVramBytes = null);

/// <summary>
///     Provider-to-host seam for report-only llama-server load telemetry. The provider supplies a null implementation;
///     the application host bridges observations to its own meter without reversing the dependency direction.
/// </summary>
public interface ILlamaServerLoadTelemetry
{
    void RecordLoad(LlamaServerLoadObservation observation);
}

internal sealed class NullLlamaServerLoadTelemetry : ILlamaServerLoadTelemetry
{
    public void RecordLoad(LlamaServerLoadObservation observation)
    {
    }
}
