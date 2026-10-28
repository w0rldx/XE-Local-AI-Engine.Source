namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog.Implementation;
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

    /// <summary>Whether External Apps start with this node: the switch is on AND the catalog listed an application at startup. Only this opens the container bridge.</summary>
    /// <remarks>
    ///     The bridge binds a container-facing, non-loopback address, so an empty catalog keeps it
    ///     closed. The runtime switch, the route family and the hub still follow the stored setting alone.
    /// </remarks>
    public bool ExternalAppsEnabled { get; init; }

    /// <summary>The switch is on but the catalog listed no application at startup, so installing waits for a node restart.</summary>
    public bool ExternalAppsAwaitingCatalog { get; init; }

    /// <summary>The whole stored record this read saw: the boot values <see cref="PendingRestartRegistry" /> compares against.</summary>
    public StoredNodeSettings StoredSettings { get; init; } = new();

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
        var dataRoot = root;
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

        var externalAppsSwitch = stored.ExternalAppsEnabled ?? configuration.GetValue($"{ExternalAppsOptions.SectionName}:Enabled", defaultValue: false);

        // The catalog cache lives under the data directory proper, never under the legacy content-root fallback above.
        var catalogListsApplications = externalAppsSwitch
                                       && ExternalAppStartupCatalog.ListsApplications(dataRoot,
                                           configuration[$"{ExternalAppCatalogOptions.SectionName}:{nameof(ExternalAppCatalogOptions.RefreshUrl)}"]);

        return new NodeStartupSettings
        {
            DevelopmentEnabled = stored.DevelopmentEnabled ?? configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true),
            SchedulerEnabled = stored.SchedulerEnabled ?? configuration.GetValue($"{SchedulerOptions.Section}:Enabled", defaultValue: true),
            ExternalAppsEnabled = catalogListsApplications,
            ExternalAppsAwaitingCatalog = externalAppsSwitch && !catalogListsApplications,
            StoredSettings = stored
        };
    }

    /// <summary>
    ///     The strict startup check: <see langword="true" /> when <c>node-settings.json</c> under <paramref name="dataDirectory" />
    ///     exists but cannot be read or parsed. A missing file is a fresh node, not a failure.
    /// </summary>
    public static async Task<bool> IsStoredFileUnreadableAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        using var store = new NodeSettingsStore(new FixedNodeDataDirectory(dataDirectory), NullLogger<NodeSettingsStore>.Instance);
        return await store.LoadStrictAsync(cancellationToken) is null;
    }

    /// <summary>The operator-facing reason the engine stopped on an unreadable settings file, naming the recovery.</summary>
    public static string DescribeUnreadableFile(string dataDirectory) =>
        $"The node settings file '{Path.Combine(dataDirectory, SettingsFileName)}' is present but cannot be read, so the engine stopped "
        + "instead of starting with default settings. Repair or delete node-settings.json, then start the engine again.";

    private sealed class FixedNodeDataDirectory : INodeDataDirectory
    {
        public FixedNodeDataDirectory(string root)
        {
            Root = root;
        }

        public string Root { get; }
    }
}
