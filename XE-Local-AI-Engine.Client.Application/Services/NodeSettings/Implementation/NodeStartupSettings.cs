namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The feature switches that decide what the host registers or binds, read once from <c>node-settings.json</c> before
///     the container exists, each as stored ?? appsettings seed ?? code default.
/// </summary>
/// <remarks>
///     Registered as a singleton by the host. A stored change to one of these applies after a node restart. The file is
///     plaintext, so this works while the vault is still locked.
/// </remarks>
public sealed class NodeStartupSettings
{
    private const string SettingsFileName = "node-settings.json";

    /// <summary>Whether Development Mode endpoints, services and hub are registered.</summary>
    public bool DevelopmentEnabled { get; init; } = true;

    /// <summary>Whether the Quartz scheduler runtime is registered.</summary>
    public bool SchedulerEnabled { get; init; } = true;

    /// <summary>Whether external apps are on, which also lets the container bridge listener open.</summary>
    public bool ExternalAppsEnabled { get; init; }

    /// <summary>Reads the stored switches for the data directory <paramref name="configuration" /> resolves.</summary>
    /// <param name="includeLegacyContentRoot">
    ///     <see langword="false" /> ignores a <c>node-settings.json</c> in the content root. A test host passes it, because its
    ///     content root is the developer's source directory.
    /// </param>
    public static NodeStartupSettings Read(IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<NodeSettingsStore>? logger = null,
        bool includeLegacyContentRoot = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Same resolution as NodeDataDirectory. Its constructor moves a legacy content-root file into the data dir on first
        // launch, but only after this read, and never over a file already there: read the file that migration would keep.
        var contentRoot = environment.ContentRootPath;
        var configuredRoot = configuration[NodeDataDirectory.ConfigurationKey];
        var root = string.IsNullOrWhiteSpace(configuredRoot) ? contentRoot : configuredRoot;
        if (includeLegacyContentRoot
            && !string.Equals(root, contentRoot, StringComparison.Ordinal)
            && !File.Exists(Path.Combine(root, SettingsFileName))
            && File.Exists(Path.Combine(contentRoot, SettingsFileName)))
        {
            root = contentRoot;
        }

        StoredNodeSettings stored;
        using (var store = new NodeSettingsStore(new FixedNodeDataDirectory(root), logger ?? NullLogger<NodeSettingsStore>.Instance))
        {
            stored = store.Load(CancellationToken.None);
        }

        return new NodeStartupSettings
        {
            DevelopmentEnabled = stored.DevelopmentEnabled ?? configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true),
            SchedulerEnabled = stored.SchedulerEnabled ?? configuration.GetValue($"{SchedulerOptions.Section}:Enabled", defaultValue: true),
            ExternalAppsEnabled = stored.ExternalAppsEnabled ?? configuration.GetValue($"{ExternalAppsOptions.SectionName}:Enabled", defaultValue: false)
        };
    }

    private sealed class FixedNodeDataDirectory : INodeDataDirectory
    {
        public FixedNodeDataDirectory(string root)
        {
            Root = root;
        }

        public string Root { get; }
    }
}
