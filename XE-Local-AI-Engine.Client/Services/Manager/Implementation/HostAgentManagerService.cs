namespace XE_Local_AI_Engine.Client.Services.Manager.Implementation;

using XE_Local_AI_Engine.Client.Services.Manager;

using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class HostAgentManagerService : IHostAgentManagerService
{
    private const string HostAgentRuntimeSectionName = "HostAgent:Runtime";

    private static readonly string[] SensitiveEnvironmentNameParts =
    [
        "SECRET",
        "TOKEN",
        "PASSWORD",
        "CREDENTIAL",
        "PRIVATE_KEY",
        "API_KEY",
        "HMAC"
    ];

    private readonly IConfiguration _configuration;

    private readonly IHostAgentClient _hostAgentClient;
    private readonly ILocalModelProvider _localModelProvider;

    public HostAgentManagerService(IHostAgentClient hostAgentClient,
        ILocalModelProvider localModelProvider,
        IConfiguration configuration)
    {
        _hostAgentClient = hostAgentClient;
        _localModelProvider = localModelProvider;
        _configuration = configuration;
    }

    public async Task<HostAgentManagerSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var statusTask = _hostAgentClient.GetStatusAsync(cancellationToken);
        var capabilitiesTask = _hostAgentClient.GetCapabilitiesAsync(cancellationToken);
        var containersTask = _hostAgentClient.ListContainersAsync(cancellationToken);
        var modelHealthTask = _localModelProvider.CheckHealthAsync(cancellationToken);
        var modelsTask = _localModelProvider.ListModelsAsync(cancellationToken);

        await Task.WhenAll(statusTask, capabilitiesTask, containersTask, modelHealthTask, modelsTask).ConfigureAwait(false);

        return new HostAgentManagerSnapshot(await statusTask.ConfigureAwait(false),
            await capabilitiesTask.ConfigureAwait(false),
            await containersTask.ConfigureAwait(false),
            await modelHealthTask.ConfigureAwait(false),
            await modelsTask.ConfigureAwait(false),
            LoadManifestView());
    }

    public Task<ContainerActionReportDto> ExecuteContainerActionAsync(string containerName,
        HostAgentContainerAction action,
        TimeSpan drainTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        return action switch
        {
            HostAgentContainerAction.Start => _hostAgentClient.StartContainerAsync(containerName, cancellationToken),
            HostAgentContainerAction.Stop => _hostAgentClient.StopContainerAsync(containerName, drainTimeout, cancellationToken),
            HostAgentContainerAction.Restart => _hostAgentClient.RestartContainerAsync(containerName, drainTimeout, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported container action.")
        };
    }

    public IAsyncEnumerable<HostAgentLogLineDto> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        CancellationToken cancellationToken)
    {
        return _hostAgentClient.StreamLogsAsync(containerName, tailLines, follow, cancellationToken);
    }

    private HostAgentManifestView LoadManifestView()
    {
        var manifestSection = _configuration.GetSection($"{HostAgentRuntimeSectionName}:Manifest");
        if (!manifestSection.Exists())
        {
            return UnavailableManifest("manifest-not-configured");
        }

        try
        {
            var manifest = manifestSection.Get<HostAgentManifest>();
            if (manifest is null)
            {
                return UnavailableManifest("manifest-not-configured");
            }

            return new HostAgentManifestView(true,
                manifest.SchemaVersion,
                manifest.RuntimeMode,
                manifest.Models.BootstrapModel,
                manifest.Models.DefaultChatModel,
                manifest.RuntimeLimits.MaxRuntimeDiskGb,
                manifest.RuntimeLimits.StopDrainTimeoutSeconds,
                manifest.Containers.Select(ToContainerView).ToArray(),
                []);
        }
        catch (InvalidOperationException exception)
        {
            return UnavailableManifest($"manifest-bind-failed:{exception.Message}");
        }
    }

    private static HostAgentManifestContainerView ToContainerView(ContainerManifest container)
    {
        return new HostAgentManifestContainerView(container.Name,
            container.Image,
            container.Network,
            container.Environment
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Select(pair => new HostAgentManifestEnvironmentView(pair.Key, RedactEnvironmentValue(pair.Key, pair.Value)))
                     .ToArray(),
            container.Volumes
                     .Select(volume => new HostAgentManifestVolumeView(volume.Source, volume.Target, volume.ReadOnly))
                     .ToArray());
    }

    private static string RedactEnvironmentValue(string name, string value)
    {
        return SensitiveEnvironmentNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))
            ? "<redacted>"
            : value;
    }

    private static HostAgentManifestView UnavailableManifest(string diagnostic)
    {
        return new HostAgentManifestView(false,
            null,
            "unknown",
            "unknown",
            "unknown",
            null,
            null,
            [],
            [diagnostic]);
    }
}
