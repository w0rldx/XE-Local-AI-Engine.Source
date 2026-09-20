namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>No usable container runtime: thrown by <see cref="IContainerRuntimeResolver.CreateRuntimeAsync" /> for every resolution that is not <c>Ready</c>.</summary>
/// <remarks>
///     There is no degraded mode, because an application container created against a daemon that failed its
///     preflight is one whose confinement was never established. It carries the whole
///     <see cref="ContainerRuntimeResolution" /> rather than only a message, so a caller reporting the failure shows
///     the operator the same status, daemon summary and prose the runtime card shows, rather than a second
///     description of the same state written at the catch site.
/// </remarks>
public sealed class ContainerRuntimeUnavailableException : Exception
{
    public ContainerRuntimeUnavailableException(ContainerRuntimeResolution resolution)
        : base(resolution?.Message ?? "No container runtime is available.")
    {
        Resolution = resolution;
    }

    public ContainerRuntimeUnavailableException(ContainerRuntimeResolution resolution, Exception innerException)
        : base(resolution?.Message ?? "No container runtime is available.", innerException)
    {
        Resolution = resolution;
    }

    public ContainerRuntimeUnavailableException(string message) : base(message)
    {
    }

    public ContainerRuntimeUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ContainerRuntimeUnavailableException()
    {
    }

    /// <summary>The resolution that refused, or null when the exception was constructed without one.</summary>
    public ContainerRuntimeResolution? Resolution { get; }
}
