namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using Microsoft.Extensions.Options;

/// <summary>Builds <see cref="DockerDotNetRuntimeClient" /> instances with the configured probe timeout.</summary>
internal sealed class DockerDotNetRuntimeClientFactory : IDockerRuntimeClientFactory
{
    private readonly IOptionsMonitor<ContainerSandboxOptions> _options;
    private readonly TimeProvider _timeProvider;

    public DockerDotNetRuntimeClientFactory(IOptionsMonitor<ContainerSandboxOptions> options, TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new DockerDotNetRuntimeClient(endpoint,
            TimeSpan.FromSeconds(_options.CurrentValue.DaemonProbeTimeoutSeconds),
            _timeProvider);
    }
}
