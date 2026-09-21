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
    ///     RAM, which says something different about a measurement than <see cref="Partial" /> does.
    /// </summary>
    /// <remarks>Appended last, so the existing ordinals are unchanged.</remarks>
    None
}

/// <summary>Whether this was the primary launch candidate or the explicit one-shot KV/FA-safe retry.</summary>
public enum LlamaServerLoadAttemptKind
{
    Primary,
    SafeRetry
}

/// <summary>
///     Observation of one llama-server load attempt. Report-only: it is no memory ledger and no admission decision, and
///     the two VRAM figures are a RECORD of what admission decided, never an input to a later one.
/// </summary>
/// <remarks>
///     Every member is content-free, but only the DIMENSIONS are bounded-cardinality: role, variant, outcome,
///     placement, attempt kind and speculative class. <see cref="ModelName" />, <see cref="RuntimeVersion" /> and
///     <see cref="RuntimeSha256" /> are identities and unbounded — a new build or a new model is a new value — so they
///     are carried for a host-side consumer to key a record on and must NOT reach a metric tag; the meter bridge
///     deliberately tags role/variant/outcome only.
/// </remarks>
public sealed class LlamaServerLoadObservation
{
    public required ModelRole Role { get; init; }

    public required GpuVariant Variant { get; init; }

    public required string RuntimeVersion { get; init; }

    public required string? RuntimeSha256 { get; init; }

    public required double ReadinessDurationMs { get; init; }

    public required LlamaServerReadinessOutcome Outcome { get; init; }

    public required LlamaServerPlacementOutcome Placement { get; init; }

    public required LlamaServerLoadAttemptKind AttemptKind { get; init; }

    public required SpeculativeModeClass SpeculativeModeClass { get; init; }

    // Required, so every construction site supplies the model name; the two long? members below stay optional, so a
    // caller that measured nothing need not set them.
    /// <summary>The model this load was for. Carried for keying only; never a metric tag.</summary>
    public required string ModelName { get; init; }

    /// <summary>
    ///     Machine-global free VRAM as the capacity gate measured it immediately before admitting THIS load, carried
    ///     here rather than re-measured (<c>ProcessLaunchAdmission.GlobalFreeVramBytesAtAdmission</c>).
    /// </summary>
    /// <remarks>
    ///     It is that gate's forced hardware re-probe under the decision gate. Null when the load carried no capacity
    ///     admission (a direct, profiling or test spawn), when the box has no readable global-free figure (a non-NVIDIA
    ///     or CPU-only host), or when the selected runtime variant moved off the one the admission was granted against.
    /// </remarks>
    public long? GlobalFreeVramBytesAtLoad { get; init; }

    /// <summary>
    ///     The GPU bytes the capacity gate RESERVED for this process — the admitted allocation's footprint, NOT
    ///     llama.cpp's own <c>--list-devices</c> process budget, a different axis not read on this path.
    /// </summary>
    /// <remarks>Zero is a real answer for a CPU-placed allocation; null means there was no admission to read.</remarks>
    public long? AdmittedVramBytes { get; init; }
}
