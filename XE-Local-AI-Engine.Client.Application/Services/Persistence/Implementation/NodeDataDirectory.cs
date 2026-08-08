namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Default <see cref="INodeDataDirectory" />: resolves the per-node runtime-state root from the
///     <c>NodeData:Directory</c> configuration key (layered in only by <c>DesktopBootstrap</c> in desktop mode) and falls
///     back to <see cref="IHostEnvironment.ContentRootPath" /> when the key is absent. Off the desktop flag the key is
///     never set, so <see cref="Root" /> equals the content root and every consuming store reads/writes exactly where it
///     did before — the off-flag byte-behavior invariant.
/// </summary>
public sealed class NodeDataDirectory : INodeDataDirectory
{
    /// <summary>Configuration key <c>DesktopBootstrap</c> layers in (in desktop mode) with the per-user data directory.</summary>
    public const string ConfigurationKey = "NodeData:Directory";

    /// <summary>The per-node runtime-state artifacts that the first-launch migration relocates ContentRoot → data dir.</summary>
    private static readonly string[] MigratableArtifacts =
    [
        "node-settings.json",
        "worker-credentials.enc",
        "cloud-credentials.enc",
        "hf-token.enc",
        "codex-oauth-tokens.enc"
    ];

    public NodeDataDirectory(IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ILogger<NodeDataDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        var contentRoot = hostEnvironment.ContentRootPath;
        var configuredRoot = configuration[ConfigurationKey];

        Root = string.IsNullOrWhiteSpace(configuredRoot) ? contentRoot : configuredRoot;

        // First-launch migration: when the data dir is distinct from the content root (i.e. desktop mode relocated it),
        // best-effort move any artifact a previously-broken RC wrote into the shared install dir so a tester keeps their
        // credentials/selection. Swallow IO errors at Debug — a missed copy degrades to "re-enter the token", never a crash.
        if (!string.Equals(Root, contentRoot, StringComparison.Ordinal))
        {
            MigrateContentRootArtifacts(contentRoot, Root, logger);
        }
    }

    /// <inheritdoc />
    public string Root { get; }

    private static void MigrateContentRootArtifacts(string contentRoot, string dataDirectory, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "The data directory could not be ensured for first-launch state migration.");
            return;
        }

        foreach (var artifact in MigratableArtifacts)
        {
            var source = Path.Combine(contentRoot, artifact);
            var destination = Path.Combine(dataDirectory, artifact);

            // The data dir wins: never clobber a file already living in the canonical location.
            if (!File.Exists(source) || File.Exists(destination))
            {
                continue;
            }

            try
            {
                File.Move(source, destination);
                logger.LogDebug("Migrated {Artifact} from the content root into the per-user data directory.", artifact);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Best-effort migration of {Artifact} into the per-user data directory failed.", artifact);
            }
        }
    }
}
