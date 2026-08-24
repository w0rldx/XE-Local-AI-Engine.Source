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
    ///     Returns the absolute path of the provisioned interpreter, provisioning the venv if it is absent or stale.
    ///     Concurrent callers share one provision rather than racing it.
    /// </summary>
    /// <exception cref="ComputeEnvironmentException">
    ///     The environment could not be provisioned, with a message phrased for the model (and therefore the operator)
    ///     that names no host path.
    /// </exception>
    Task<string> GetInterpreterPathAsync(CancellationToken cancellationToken = default);
}

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
