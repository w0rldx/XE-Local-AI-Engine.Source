namespace XE_Local_AI_Engine.Client.Services.Containers.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

/// <summary>
///     Builds <see cref="IContainerRuntime" /> clients sized for application containers rather than for a build
///     sandbox.
///     <para>
///         It exists beside <see cref="DockerDotNetRuntimeClientFactory" /> rather than replacing it because the two
///         consumers have opposite time budgets. Development Mode's factory builds a client whose only deadline is a
///         ten-second preflight; an application install pulls a multi-gigabyte image and stops a container gracefully,
///         so this one sizes the transport timeout from the stop grace and gives the pull its own half-hour deadline.
///         One shared factory would have to be sized for the slower of the two, which would leave a dead Development
///         Mode preflight hanging for minutes.
///     </para>
/// </summary>
internal sealed class DockerContainerRuntimeFactory : IContainerRuntimeFactory
{
    /// <summary>
    ///     Headroom added to the stop grace when sizing the transport timeout, so the daemon's own kill-after-grace
    ///     always lands inside the HTTP request rather than the request timing out first and leaving the caller unable
    ///     to say whether the container stopped.
    /// </summary>
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

        // Categorised as the client rather than as this factory: the two things it logs — an unrecognised daemon
        // health string, and a failed write probe — are the client's observations, and an operator reading them needs
        // the name of the type that made the wire call.
        _clientLogger = loggerFactory.CreateLogger<DockerDotNetRuntimeClient>();
    }

    /// <summary>
    ///     The inherited Development Mode shape. It returns the same client this layer's own creator does, so a caller
    ///     that reached this factory through the base interface still gets the container-runtime timeouts rather than
    ///     silently getting the preflight's.
    /// </summary>
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
