namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Worker-side compute-tool configuration (section <c>Compute</c>).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Enabled" /> is the node kill-switch and defaults to <see langword="false" />, which is what makes
///         the feature fail closed everywhere including Production: a stripped or defaulted configuration never grants
///         a model the ability to execute code on the node. It mirrors <c>AgentHome:Enabled</c> and is read the same way.
///     </para>
///     <para>
///         The ceilings are deliberately tighter than AgentHome's. A research loop calls this tool many times for a
///         second or two each, not once for ten minutes, so a short wall clock is a feature: it turns an accidental
///         infinite loop into a fast, reported failure the model can correct instead of a stalled turn.
///     </para>
/// </remarks>
public sealed class ComputeOptions
{
    /// <summary>The configuration section this options type binds to.</summary>
    public const string SectionName = "Compute";

    /// <summary>Whether the sandboxed <c>run_python</c> tool may execute on this node. Off unless explicitly enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Wall-clock ceiling for a single script, after which the process tree is killed. Defaults to 30 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Per-stream byte ceiling on the model-facing stdout/stderr, truncated with a marker. Defaults to 65536.</summary>
    public int MaxOutputBytes { get; set; } = 65536;

    /// <summary>Resident-memory ceiling for the sandbox, applied only where the host can enforce it. Defaults to 2048 MB.</summary>
    public int MemoryMb { get; set; } = 2048;

    /// <summary>CPU-core ceiling for the sandbox, applied only where the host can enforce it. Defaults to 2.</summary>
    public double CpuCount { get; set; } = 2;

    /// <summary>Process/thread ceiling for the sandbox, applied only where the host can enforce it. Defaults to 64.</summary>
    public int PidsLimit { get; set; } = 64;
}
