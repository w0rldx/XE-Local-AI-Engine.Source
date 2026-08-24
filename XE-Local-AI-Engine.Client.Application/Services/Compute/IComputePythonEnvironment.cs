namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Resolves the interpreter the compute tool executes scripts with, provisioning it on first use. There is exactly
///     one such interpreter per box and it is never the host's Python (ADR 0005): it is a uv-managed, digest-pinned,
///     lockfile-driven venv the engine owns, so what <c>import numpy</c> resolves to does not depend on whatever the
///     operator happens to have installed.
/// </summary>
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
/// <param name="InterpreterPath">
///     The absolute host path of the venv's own interpreter — deliberately NOT the managed CPython it links to. The
///     venv path is what makes <c>sys.prefix</c> the provisioned closure; exec'ing the real binary directly would
///     resolve <c>import numpy</c> against the bare interpreter's own (empty) site-packages instead.
/// </param>
/// <param name="ReadOnlyTrees">
///     The trees to bind, at their own canonical paths. Deliberately the two smallest that make the interpreter work
///     — the venv and the managed-CPython root it links into — and never the directory above them, which also holds
///     the uv cache and the lockfile state a later call would otherwise inherit.
/// </param>
internal sealed record ComputePythonRuntime(string InterpreterPath, IReadOnlyList<string> ReadOnlyTrees);

/// <summary>
///     A compute-environment failure whose message is model-safe <b>by contract</b>: every construction site phrases it
///     for an operator and names no path, URL or environment value. The gateway surfaces these verbatim to the model and
///     collapses every other exception to a generic reason, so widening that guarantee here widens what the model sees.
/// </summary>
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
