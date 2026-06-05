namespace XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Linux.Capabilities;

public sealed class RootlessDockerBootstrapHostedService : IHostedService
{
    private readonly ILogger<RootlessDockerBootstrapHostedService> _logger;
    private readonly HostAgentDockerOptions _options;
    private readonly IProcessRunner _processRunner;

    public RootlessDockerBootstrapHostedService(IOptions<HostAgentDockerOptions> options,
        IProcessRunner processRunner,
        ILogger<RootlessDockerBootstrapHostedService> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.UseFakeDriver || DockerSocketExists(_options.Endpoint))
        {
            return;
        }

        var result = await _processRunner.RunAsync("dockerd-rootless-setuptool.sh",
            ["install"],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"dockerd-rootless-setuptool.sh install failed with exit code {result.ExitCode}.");
        }

        _logger.LogInformation("Rootless Docker setup completed for HostAgent.Linux first start.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static bool DockerSocketExists(string endpoint)
    {
        const string unixPrefix = "unix://";
        return endpoint.StartsWith(unixPrefix, StringComparison.OrdinalIgnoreCase)
               && File.Exists(endpoint[unixPrefix.Length..]);
    }
}
