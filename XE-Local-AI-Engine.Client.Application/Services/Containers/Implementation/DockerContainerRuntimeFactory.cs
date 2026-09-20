namespace XE_Local_AI_Engine.Client.Services.Containers.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

/// <summary>Builds <see cref="IContainerRuntime" /> clients sized for application containers rather than for a build sandbox.</summary>
/// <remarks>
///     It stands beside <see cref="DockerDotNetRuntimeClientFactory" /> rather than replacing it, because the two
///     consumers have opposite time budgets: that factory's only deadline is a ten-second preflight, while an
///     application install pulls a multi-gigabyte image and stops a container gracefully, so this one sizes the
///     transport timeout from the stop grace and gives the pull its own half-hour deadline. One shared factory sized
///     for the slower of the two would leave a dead Development Mode preflight hanging for minutes.
/// </remarks>
internal sealed class DockerContainerRuntimeFactory : IContainerRuntimeFactory
{
    /// <summary>Headroom added to the stop grace when sizing the transport timeout.</summary>
    /// <remarks>
    ///     The daemon's own kill-after-grace then always lands inside the HTTP request, rather than the request
    ///     timing out first and leaving the caller unable to say whether the container stopped.
    /// </remarks>
    private static readonly TimeSpan StopTimeoutHeadroom = TimeSpan.FromSeconds(5);

    private readonly ILogger _clientLogger;
    private readonly IOptionsMonitor<ContainerRuntimeOptions> _options;
    private readonly TimeProvider _timeProvider;

    public DockerContainerRuntimeFactory(IOptionsMonitor<ContainerRuntimeOptions> options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // Categorised as the client, not this factory: the two things it logs — an unrecognised daemon health string
        // and a failed write probe — are the client's observations, and an operator needs the wire caller's name.
        _clientLogger = loggerFactory.CreateLogger<DockerDotNetRuntimeClient>();
    }

    /// <summary>The inherited Development Mode shape, returning the same client this layer's own creator does.</summary>
    /// <remarks>
    ///     A caller that reached this factory through the base interface therefore still gets the container-runtime
    ///     timeouts rather than silently getting the preflight's.
    /// </remarks>
    public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
    {
        return CreateRuntime(endpoint);
    }

    public IContainerRuntime CreateRuntime(DockerDaemonEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var options = _options.CurrentValue;
        var probeTimeout = TimeSpan.FromSeconds(options.DaemonProbeTimeoutSeconds);
        var stopTimeout = TimeSpan.FromSeconds(options.StopGracePeriodSeconds) + StopTimeoutHeadroom;

        return new DockerDotNetRuntimeClient(endpoint,
            probeTimeout,
            _timeProvider,
            probeTimeout > stopTimeout ? probeTimeout : stopTimeout,
            TimeSpan.FromMinutes(options.PullTimeoutMinutes),
            _clientLogger);
    }
}
