namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Worker-side compute-tool configuration (section <c>Compute</c>).
/// </summary>
/// <remarks>
///     <see cref="Enabled" /> is the node kill-switch and defaults to <see langword="false" />, which is what makes the
///     feature fail closed everywhere including Production: a stripped or defaulted configuration never grants a model
///     the ability to execute code on the node. It mirrors <c>AgentHome:Enabled</c>. The ceilings are deliberately
///     tighter than AgentHome's — a research loop calls this tool many times for a second or two each, so a short wall
///     clock turns an accidental infinite loop into a fast, reported failure the model can correct.
/// </remarks>
public sealed class ComputeOptions
{
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

    /// <summary>
    ///     What every numeric-library thread-count variable (<c>OMP_NUM_THREADS</c> and its siblings) is pinned to
    ///     inside the sandbox. Defaults to <c>min(4, processor count)</c>.
    /// </summary>
    /// <remarks>
    ///     Pinned rather than left to the libraries, which size their pools from the HOST's core count read out of
    ///     <c>/proc</c> — not what <see cref="CpuCount" /> allows, so an unpinned BLAS starts a thread per host core and
    ///     then thrashes inside a fraction of one. The cap at four is the other half: a linear-algebra call on the array
    ///     sizes this tool sees stops scaling long before a many-core box's core count, and the threads it would start
    ///     still cost against <see cref="PidsLimit" />.
    /// </remarks>
    public int ThreadLimit { get; set; } = Math.Min(val1: 4, Environment.ProcessorCount);

    /// <summary>
    ///     How many bytes a single script may write into its own jail before the process tree is terminated. Defaults
    ///     to 256 MiB.
    /// </summary>
    /// <remarks>
    ///     Far tighter than the node-wide sandbox ceiling, because arithmetic and symbolic algebra write almost
    ///     nothing, so a script filling hundreds of megabytes is a runaway rather than a workload. It can only TIGHTEN:
    ///     the provider applies the smaller of this and the node-wide <c>LocalContainer:MaxJailDiskBytes</c>. It covers
    ///     EVERYTHING the script can write, because <c>HOME</c> and <c>TMPDIR</c> are directories inside that same jail
    ///     — a scratch elsewhere would read as a bound and not be one. See docs/wiki/19-compute-tools.md, "2.1 Execution flow".
    /// </remarks>
    public long MaxJailDiskBytes { get; set; } = 256L * 1024 * 1024;
}
