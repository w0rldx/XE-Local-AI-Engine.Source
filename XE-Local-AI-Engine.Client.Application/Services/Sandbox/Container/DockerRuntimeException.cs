namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     A Docker daemon interaction failed, classified into the outcome an operator can act on. Carrying the
///     <see cref="Status" /> rather than only a message is what lets the preflight distinguish "no daemon" from
///     "permission denied" from "too old" without pattern-matching on daemon prose, which changes between releases.
/// </summary>
public sealed class DockerRuntimeException : Exception
{
    public DockerRuntimeException(DockerDaemonPreflightStatus status, string message) : base(message)
    {
        Status = status;
    }

    public DockerRuntimeException(DockerDaemonPreflightStatus status, string message, Exception innerException)
        : base(message, innerException)
    {
        Status = status;
    }

    public DockerRuntimeException(string message) : base(message)
    {
        Status = DockerDaemonPreflightStatus.ProbeFailed;
    }

    public DockerRuntimeException(string message, Exception innerException) : base(message, innerException)
    {
        Status = DockerDaemonPreflightStatus.ProbeFailed;
    }

    public DockerRuntimeException()
    {
        Status = DockerDaemonPreflightStatus.ProbeFailed;
    }

    /// <summary>The classified outcome.</summary>
    public DockerDaemonPreflightStatus Status { get; }
}
