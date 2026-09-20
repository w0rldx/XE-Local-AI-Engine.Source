namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Resolves the interpreter the compute tool executes scripts with, provisioning it on first use.
/// </summary>
/// <remarks>
///     There is exactly one such interpreter per box and it is never the host's Python (ADR 0005): it is a uv-managed,
///     digest-pinned, lockfile-driven venv the engine owns, so what <c>import numpy</c> resolves to does not depend on
///     whatever the operator happens to have installed.
/// </remarks>
internal interface IComputePythonEnvironment
{
    /// <summary>
    ///     Returns the provisioned runtime, provisioning the venv if it is absent or stale. Concurrent callers share
    ///     one provision rather than racing it.
    /// </summary>
    /// <exception cref="ComputeEnvironmentException">
    ///     The environment could not be provisioned, with a message phrased for the model (and therefore the operator)
    ///     that names no host path.
    /// </exception>
    Task<ComputePythonRuntime> GetRuntimeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     The provisioned interpreter, plus the host trees a filesystem-isolated sandbox has to bind read-only for it to
///     run at all.
/// </summary>
internal sealed class ComputePythonRuntime
{
    /// <summary>
    ///     The absolute host path of the venv's own interpreter — deliberately NOT the managed CPython it links to.
    /// </summary>
    /// <remarks>
    ///     The venv path is what makes <c>sys.prefix</c> the provisioned closure; exec'ing the real binary directly
    ///     would resolve <c>import numpy</c> against the bare interpreter's own (empty) site-packages instead.
    /// </remarks>
    public required string InterpreterPath { get; init; }

    /// <summary>
    ///     The trees to bind, at their own canonical paths.
    /// </summary>
    /// <remarks>
    ///     Deliberately the two smallest that make the interpreter work — the venv and the managed-CPython root it
    ///     links into — and never the directory above them, which also holds the uv cache and the lockfile state a
    ///     later call would otherwise inherit.
    /// </remarks>
    public required IReadOnlyList<string> ReadOnlyTrees { get; init; }
}

/// <summary>
///     A compute-environment failure whose message is model-safe <b>by contract</b>: every construction site phrases it
///     for an operator and names no path, URL or environment value.
/// </summary>
/// <remarks>
///     The gateway surfaces these verbatim to the model and collapses every other exception to a generic reason, so
///     widening that guarantee here widens what the model sees.
/// </remarks>
public sealed class ComputeEnvironmentException : Exception
{
    public ComputeEnvironmentException()
    {
    }

    public ComputeEnvironmentException(string message)
        : base(message)
    {
    }

    public ComputeEnvironmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
