namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The node's one door to installed external applications: admission, the state machine, the shared rebuild block every pipeline re-enters, and the API's projections.</summary>
/// <remarks>
///     A SINGLETON holding an <see cref="IServiceScopeFactory" />, never a scoped service holding a store.
///     Admission and execution each open their own scope, so no request-scoped database context reaches a pipeline
///     that outlives the request — the browser cannot dispose the context an install is about to compare-and-swap
///     with by navigating away.
/// </remarks>
internal sealed partial class ExternalAppService
{
    private const string SecretVariableType = "secret";
    private const int MaxFailureSummaryLength = 512;
    private const string LogTruncationMarker = "[earlier output omitted]";

    /// <summary>
    ///     What a row whose stored manifest cannot be read is marked with. It names no JSON, no path and no value:
    ///     it is rendered in a browser next to the instance's own card.
    /// </summary>
    private const string UnreadableManifestSummary = "The stored manifest for this application could not be read.";

    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The baseline an install's acknowledgement payload is diffed against: an application that grants nothing.</summary>
    /// <remarks>
    ///     Diffing the manifest against it yields every name it DOES grant, which is what "the whole effective
    ///     permission set" means without a second derivation that could disagree with <c>Diff</c>.
    /// </remarks>
    private static readonly ExternalAppEffectivePermissions NoPermissions = new()
    {
        Internet = false,
        LocalNetwork = false,
        HostFiles = "none",
        Gpu = "none",
        Services = new Dictionary<string, ExternalAppServicePermissions>(StringComparer.Ordinal)
    };

    private readonly ExternalAppInstanceGate _gate;
    private readonly string _installId;
    private readonly ExternalAppStorageLayout _layout;
    private readonly ILogger<ExternalAppService> _logger;
    private readonly ExternalAppsOptions _options;
    private readonly IExternalAppEventPublisher _publisher;
    private readonly ExternalAppResourceGate _resourceGate;
    private readonly ExternalAppOperationRunner _runner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ContainerBridgeEndpointSource _bridgeEndpoints;
    private readonly TimeProvider _timeProvider;

    public ExternalAppService(IServiceScopeFactory scopeFactory,
        ExternalAppStorageLayout layout,
        ExternalAppResourceGate resourceGate,
        ExternalAppInstanceGate gate,
        ExternalAppOperationRunner runner,
        IExternalAppEventPublisher publisher,
        INodeDataDirectory dataDirectory,
        IOptions<ExternalAppsOptions> options,
        ContainerBridgeEndpointSource bridgeEndpoints,
        TimeProvider timeProvider,
        ILogger<ExternalAppService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);

        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _resourceGate = resourceGate ?? throw new ArgumentNullException(nameof(resourceGate));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options.Value;
        _bridgeEndpoints = bridgeEndpoints ?? throw new ArgumentNullException(nameof(bridgeEndpoints));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The same per-installation id Development Mode labels its containers with, from the same directory: two
        // installations on one daemon must not remove each other's containers, and the owner label is a constant.
        _installId = DockerSandboxRuntimeProvider.BuildInstallId(dataDirectory.Root);
    }

    /// <summary>The bridge grant for one instance, or <see langword="null" /> when this node has no open bridge or the instance carries no token.</summary>
    /// <remarks>
    ///     A token-less instance is one installed before the bridge existed. The two halves come from different
    ///     places — the endpoint from the composition root, the token from the encrypted row — and a grant is only
    ///     ever built from both.
    /// </remarks>
    private async Task<ContainerBridgeGrant?> ResolveBridgeGrantAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (_bridgeEndpoints.Current is null)
        {
            // No bridge on this node, so nothing to read the row for.
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>();
        var row = await store.GetAsync(instanceId, cancellationToken);

        return BridgeGrantFor(row?.BridgeToken);
    }

    /// <summary>Whether <paramref name="manifest" /> needs the container bridge and this node did not open one.</summary>
    /// <remarks>
    ///     That is the state <c>DeploymentPlanner.Plan</c> would later report as an unresolvable token. Asked at
    ///     admission by the install and the update path alike, so a preview can render it and a command can refuse
    ///     it before a row is written or a running version is stopped.
    /// </remarks>
    private bool BridgeUnavailableFor(ApplicationManifest manifest)
    {
        return _bridgeEndpoints.Current is null && DeploymentPlanner.RequiresBridge(manifest);
    }

    /// <summary>The same question for a stored row.</summary>
    /// <remarks>
    ///     The bridge check comes first, so a node that opened one never pays the snapshot deserialize — nor
    ///     inherits its failure mode — for an answer that is already "no". Internal rather than private because the
    ///     boot reconciler asks it too: a row whose plan cannot be rebuilt for want of a bridge must be told that,
    ///     not told to start it again, which is the very command the Start/Restart admission below refuses.
    /// </remarks>
    internal bool BridgeUnavailableFor(ExternalAppInstanceSnapshot row)
    {
        if (_bridgeEndpoints.Current is not null)
        {
            return false;
        }

        try
        {
            return BridgeUnavailableFor(DeserializeManifest(row.ManifestSnapshotJson));
        }
        catch (Exception exception) when (exception is JsonException or ExternalAppManifestException)
        {
            // Admission cannot say whether an unreadable manifest needs the bridge, so it does not: the command is
            // admitted and the pipeline records the failure on the row, never a bridge refusal it cannot justify.
            return false;
        }
    }

    /// <summary>
    ///     The grant for a token already in hand, so the Start path that rebuilds from a row it has just read does
    ///     not read that row a second time.
    /// </summary>
    private ContainerBridgeGrant? BridgeGrantFor(string? bridgeToken)
    {
        return _bridgeEndpoints.Current is { } endpoint && bridgeToken is { Length: > 0 } token
            ? new ContainerBridgeGrant(endpoint.ContainerFacingEndpoint, token)
            : null;
    }

    /// <summary>This installation's label value, so the boot reconciler and the state observer filter on the same id the pipelines label with.</summary>
    /// <remarks>
    ///     Derived once, in one place: two derivations that drifted would make one of them unable to find what the
    ///     other created.
    /// </remarks>
    internal string InstallId => _installId;

    public async Task<IReadOnlyList<ExternalAppInstanceSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        var rows = await services.Store.ListAsync(cancellationToken);
        var versions = await ReadCatalogVersionsAsync(services.Catalog, cancellationToken);

        return [.. rows.Select(row => ToSummary(row, versions))];
    }

    public async Task<IReadOnlyList<ExternalAppInstanceDetail>> ListDetailsAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        var rows = await services.Store.ListAsync(cancellationToken);
        var versions = await ReadCatalogVersionsAsync(services.Catalog, cancellationToken);

        return [.. rows.Select(row => ToDetail(row, ToSummary(row, versions)))];
    }

    public async Task<ExternalAppInstanceDetail> GetAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        var row = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
        var versions = await ReadCatalogVersionsAsync(services.Catalog, cancellationToken);

        return ToDetail(row, ToSummary(row, versions));
    }

    public async Task<IReadOnlyList<ExternalAppInstanceEventSnapshot>> ListEventsAsync(Guid instanceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        // The existence check is not redundant: an unknown instance must answer "no such instance" rather than an
        // empty page, which a caller would render as "nothing has happened yet".
        _ = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);

        return await services.Store.ListEventsAsync(instanceId, afterSequence, limit, cancellationToken);
    }

    public async Task<ContainerLogSnapshot> ReadLogsAsync(Guid instanceId,
        string? service,
        int tail,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        if (tail < 1 || tail > _options.MaxLogTailLines)
        {
            // Rejected rather than clamped: a caller that asked for more than the node serves has to learn that its
            // window is not the window it reasoned about, not silently receive a different one.
            throw new ExternalAppValidationException(string.Create(CultureInfo.InvariantCulture,
                $"A log tail must be between 1 and {_options.MaxLogTailLines} lines."));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = ScopedServices.From(scope.ServiceProvider);

        var row = await RequireInstanceAsync(services.Store, instanceId, cancellationToken);
        var manifest = DeserializeManifest(row.ManifestSnapshotJson);
        var serviceName = SelectLogService(manifest, service);

        await using var runtime = await services.Resolver.CreateRuntimeAsync(cancellationToken: cancellationToken);
        var containers = await runtime
                               .ListContainersAsync(ExternalAppLabels.For(_installId, instanceId, serviceName), cancellationToken);
        if (containers.Count == 0)
        {
            return new ContainerLogSnapshot
            {
                Text = string.Empty,
                Truncated = false,
                LineCount = 0
            };
        }

        var snapshot = await runtime.ReadLogsAsync(containers[0],
                                        new ContainerLogRequest
                                        {
                                            TailLines = tail,
                                            MaxBytes = ContainerLogRequest.MaximumBytes
                                        },
                                        cancellationToken);

        // Logs cross UNMASKED by design: the text is the application's own container output rather than an
        // engine-owned value, and an application printing its own secrets is something its operator needs to see.
        return snapshot.Truncated
            ? snapshot with
            {
                Text = LogTruncationMarker + Environment.NewLine + snapshot.Text
            }
            : snapshot;
    }

    private static string SelectLogService(ApplicationManifest manifest, string? requested)
    {
        if (requested is not null)
        {
            return manifest.Services.Any(candidate => string.Equals(candidate.Name, requested, StringComparison.Ordinal))
                ? requested
                : throw new ExternalAppNotFoundException($"This application declares no service named '{requested}'.");
        }

        if (manifest.Services.Count == 0)
        {
            throw new ExternalAppNotFoundException("This application declares no services.");
        }

        var withUi = manifest.Services.FirstOrDefault(candidate => candidate.Ports.Count > 0);
        return (withUi ?? manifest.Services[0]).Name;
    }

    private static async Task<ExternalAppInstanceSnapshot> RequireInstanceAsync(IExternalAppInstanceStore store,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        return await store.GetAsync(instanceId, cancellationToken)
               ?? throw new ExternalAppNotFoundException($"No external application instance '{instanceId:N}' is installed.");
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadCatalogVersionsAsync(IApplicationCatalogProvider catalog,
        CancellationToken cancellationToken)
    {
        // Read once per call rather than once per row: the provider serves one snapshot, and a per-row read would
        // make a list of ten instances ten chances to observe a refresh landing mid-projection.
        var snapshot = await catalog.GetCatalogAsync(cancellationToken);
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var application in snapshot.Document.Applications)
        {
            versions[application.Id] = application.ManifestVersion;
        }

        return versions;
    }

    private static ExternalAppInstanceSummary ToSummary(ExternalAppInstanceSnapshot row,
        IReadOnlyDictionary<string, int> catalogVersions)
    {
        var known = catalogVersions.TryGetValue(row.ApplicationId, out var available);
        var updateAvailable = known && available > row.ManifestVersion;

        return new ExternalAppInstanceSummary
        {
            Id = row.Id,
            ApplicationId = row.ApplicationId,
            DisplayName = row.DisplayName,
            ManifestVersion = row.ManifestVersion,
            Status = row.Status,
            DesiredState = row.DesiredState,
            FailureCategory = row.FailureCategory,
            FailureSummary = row.FailureSummary,
            UpdateAvailable = updateAvailable,
            AvailableManifestVersion = updateAvailable ? available : null,
            CatalogMissing = !known,
            UpdatedAtUtc = row.UpdatedAtUtc,
            Version = row.Version
        };
    }

    private static ExternalAppInstanceDetail ToDetail(ExternalAppInstanceSnapshot row, ExternalAppInstanceSummary summary)
    {
        ApplicationManifest manifest;
        try
        {
            manifest = DeserializeManifest(row.ManifestSnapshotJson);
        }
        catch (Exception exception) when (exception is JsonException or ExternalAppManifestException)
        {
            return Unreadable(row, summary);
        }

        return new ExternalAppInstanceDetail
        {
            Summary = summary,
            Manifest = Sanitize(manifest),
            TestedVersion = manifest.TestedVersion,
            MaskedVariables = MaskVariables(manifest, ParseVariables(row.VariablesJson)),
            PublishedPorts = ExternalAppPublishedPorts.Parse(row.PublishedPortsJson),
            RuntimeProvider = row.RuntimeProvider,
            RuntimeOverride = row.RuntimeOverride,
            StoragePath = row.StoragePath,
            LastSequence = row.LastSequence,
            NeedsRecreate = row.NeedsRecreate,
            InstalledAtUtc = row.InstalledAtUtc,
            StartedAtUtc = row.StartedAtUtc,
            StoppedAtUtc = row.StoppedAtUtc
        };
    }

    /// <summary>One row whose stored manifest snapshot cannot be read, projected instead of thrown out of.</summary>
    /// <remarks>
    ///     The list projects every row through <see cref="ToDetail" />, so one corrupt snapshot thrown from here
    ///     would take the whole list with it — every healthy instance, and the uninstall that is the only way to be
    ///     rid of the bad row. It degrades the way <c>CatalogMissing</c> does: the row still renders and its
    ///     lifecycle controls stay reachable. The manifest is EMPTY apart from the three members the row itself
    ///     carries, and the variables are empty rather than masked, because "which are secret" is a manifest fact.
    /// </remarks>
    private static ExternalAppInstanceDetail Unreadable(ExternalAppInstanceSnapshot row, ExternalAppInstanceSummary summary)
    {
        var manifest = new ApplicationManifest(row.ApplicationId,
            row.ManifestVersion,
            string.Empty,
            row.DisplayName,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            new ApplicationPermissions(Internet: false, LocalNetwork: false, "none", "none"),
            new ApplicationResources(MinimumMemoryMb: 0, RecommendedMemoryMb: 0, CpuHint: 0, PidsLimit: 0),
            [],
            []);

        return new ExternalAppInstanceDetail
        {
            Summary = summary with
            {
                // Content-free by construction, and carried on the members the surface already renders a failure
                // banner from, so the marker needs no second wire field nobody else reads.
                FailureCategory = ExternalAppFailureCategory.Unknown,
                FailureSummary = UnreadableManifestSummary
            },
            Manifest = manifest,
            TestedVersion = null,
            MaskedVariables = new Dictionary<string, string>(StringComparer.Ordinal),
            PublishedPorts = ExternalAppPublishedPorts.Parse(row.PublishedPortsJson),
            RuntimeProvider = row.RuntimeProvider,
            RuntimeOverride = row.RuntimeOverride,
            StoragePath = row.StoragePath,
            LastSequence = row.LastSequence,
            NeedsRecreate = row.NeedsRecreate,
            InstalledAtUtc = row.InstalledAtUtc,
            StartedAtUtc = row.StartedAtUtc,
            StoppedAtUtc = row.StoppedAtUtc
        };
    }

    /// <summary>The installed snapshot as it may leave the node: asset bodies stripped and secret defaults nulled.</summary>
    /// <remarks>
    ///     Asset bodies are catalog content rather than instance state, and one of them is a several-kilobyte base64
    ///     blob on every render. Nulling secret defaults is what stops a manifest that ships a placeholder password
    ///     from handing it back as a rendered default.
    /// </remarks>
    private static ApplicationManifest Sanitize(ApplicationManifest manifest)
    {
        return manifest with
        {
            Services =
            [
                .. manifest.Services.Select(static service => service with
                {
                    Files = []
                })
            ],
            Variables =
            [
                .. manifest.Variables.Select(static variable =>
                    string.Equals(variable.Type, SecretVariableType, StringComparison.Ordinal)
                        ? variable with
                        {
                            Default = null
                        }
                        : variable)
            ]
        };
    }

    private static IReadOnlyDictionary<string, string> MaskVariables(ApplicationManifest manifest,
        IReadOnlyDictionary<string, string> values)
    {
        var secrets = manifest.Variables
                              .Where(static variable => string.Equals(variable.Type, SecretVariableType, StringComparison.Ordinal))
                              .Select(static variable => variable.Name)
                              .ToHashSet(StringComparer.Ordinal);

        var masked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            masked[entry.Key] = secrets.Contains(entry.Key) ? ExternalAppVariableMask.Value : entry.Value;
        }

        return masked;
    }

    /// <summary>Whether a container the daemon reports was built from the image this instance installed.</summary>
    /// <remarks>
    ///     Exact, because the runtime client maps the inspection's <c>Config.Image</c> — the digest-pinned reference
    ///     the container was created from — rather than the resolved image id. An id, another reference or an empty
    ///     string from a daemon that told us nothing is a container whose provenance this instance cannot establish;
    ///     all three fail, and the recovery is the rebuild a Start performs.
    /// </remarks>
    internal static bool ImageMatches(string observed, string requested)
    {
        return string.Equals(observed, requested, StringComparison.Ordinal);
    }

    private static string SerializeManifest(ApplicationManifest manifest)
    {
        return JsonSerializer.Serialize(manifest, ExternalAppJson.Options);
    }

    /// <summary>Internal so the boot reconciler and the state observer read a stored snapshot the way the pipelines do.</summary>
    internal static ApplicationManifest DeserializeManifest(string json)
    {
        return JsonSerializer.Deserialize<ApplicationManifest>(json, ExternalAppJson.Options)
               ?? throw new ExternalAppManifestException("The stored manifest snapshot could not be read.");
    }

    /// <inheritdoc cref="DeserializeManifest" />
    internal static IReadOnlyDictionary<string, string> ParseVariables(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, ExternalAppJson.Options)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static string SerializeVariables(IReadOnlyDictionary<string, string> variables)
    {
        // Never null and never absent: the column is required, and an application with no declared variables stores
        // an empty object.
        return JsonSerializer.Serialize(variables, ExternalAppJson.Options);
    }

    /// <summary>
    ///     Checks supplied values against the manifest's declarations and returns the map to store. Every refusal
    ///     names the VARIABLES, never their values: this message reaches a browser and a log file.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ValidateVariables(ApplicationManifest manifest,
        IReadOnlyDictionary<string, string> supplied)
    {
        var declared = manifest.Variables.ToDictionary(static variable => variable.Name, StringComparer.Ordinal);
        var offenders = new List<string>();

        // An undeclared key is a refusal rather than a silent drop: the one thing a user most often mistypes is the
        // name of the field holding their password, and dropping it would install an application with a blank one.
        offenders.AddRange(supplied.Keys.Where(key => !declared.ContainsKey(key)).Order(StringComparer.Ordinal));

        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in manifest.Variables)
        {
            var present = supplied.TryGetValue(variable.Name, out var value) && !string.IsNullOrEmpty(value);
            if (!present)
            {
                if (variable.Required && variable.Default is null)
                {
                    offenders.Add(variable.Name);
                }

                continue;
            }

            if (IsAcceptable(variable, value!))
            {
                accepted[variable.Name] = value!;
            }
            else
            {
                offenders.Add(variable.Name);
            }
        }

        if (offenders.Count > 0)
        {
            throw new ExternalAppValidationException($"These configuration values are missing or not valid: {string.Join(", ", offenders)}.",
                offenders);
        }

        return accepted;
    }

    private static bool IsAcceptable(ApplicationVariable variable, string value)
    {
        var typeOk = variable.Type switch
        {
            "integer" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "boolean" => bool.TryParse(value, out _),
            "enum" => variable.AllowedValues is { } allowed && allowed.Contains(value, StringComparer.Ordinal),
            _ => true
        };

        if (!typeOk)
        {
            return false;
        }

        if (variable.Validation is not { } validation)
        {
            return true;
        }

        if (validation.MinLength is { } minimum && value.Length < minimum)
        {
            return false;
        }

        if (validation.MaxLength is { } maximum && value.Length > maximum)
        {
            return false;
        }

        return validation.Pattern is not { } pattern || MatchesPattern(pattern, value);
    }

    private static bool MatchesPattern(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException exception)
        {
            // A pattern the catalog validator would have rejected, reaching us through a snapshot an older build
            // admitted. That is a manifest this node cannot use, not a value the user got wrong.
            throw new ExternalAppManifestException("A variable in this application's manifest declares a pattern this node cannot compile.",
                exception);
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new ExternalAppsDisabledException();
        }
    }

    private long Now()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    /// <summary>The three per-scope dependencies every entry point and every pipeline resolves, resolved in one place.</summary>
    private sealed record ScopedServices
    {
        public required IExternalAppInstanceStore Store { get; init; }

        public required IApplicationCatalogProvider Catalog { get; init; }

        public required IContainerRuntimeResolver Resolver { get; init; }

        public static ScopedServices From(IServiceProvider provider)
        {
            return new ScopedServices
            {
                Store = provider.GetRequiredService<IExternalAppInstanceStore>(),
                Catalog = provider.GetRequiredService<IApplicationCatalogProvider>(),
                Resolver = provider.GetRequiredService<IContainerRuntimeResolver>()
            };
        }
    }

    /// <summary>
    ///     What a pipeline believes the row currently is. Threaded through every compare-and-swap so the next write
    ///     states the version and the status it expects, instead of re-reading and hoping nothing moved in between.
    /// </summary>
    private sealed class InstanceCursor
    {
        public InstanceCursor(Guid instanceId, long version, ExternalAppInstanceStatus status)
        {
            InstanceId = instanceId;
            Version = version;
            Status = status;
        }

        public Guid InstanceId { get; }

        public long Version { get; set; }

        public ExternalAppInstanceStatus Status { get; set; }

        /// <summary>The last sequence the store minted for this instance. Uninstall's final ping is the only reader.</summary>
        public long Sequence { get; set; }
    }
}
