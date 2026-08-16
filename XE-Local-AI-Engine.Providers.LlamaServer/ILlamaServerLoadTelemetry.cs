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
///     Content-free, bounded-cardinality observation of one llama-server load attempt. It is report-only: consumers must
///     not use it as a memory ledger or an admission decision.
/// </summary>
public sealed record LlamaServerLoadObservation(
    ModelRole Role,
    GpuVariant Variant,
    string RuntimeVersion,
    string? RuntimeSha256,
    double ReadinessDurationMs,
    LlamaServerReadinessOutcome Outcome,
    LlamaServerPlacementOutcome Placement,
    LlamaServerLoadAttemptKind AttemptKind,
    SpeculativeModeClass SpeculativeModeClass);

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
