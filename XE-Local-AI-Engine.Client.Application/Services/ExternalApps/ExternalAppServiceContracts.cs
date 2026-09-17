namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Why an install or an update cannot proceed. Seven members, shared by both previews: the first five are the
///     admission checks an attempt would fail, <see cref="AlreadyInstalled" /> is reachable only from the install
///     preview and <see cref="CatalogMissing" /> only from the update preview.
/// </summary>
public enum ExternalAppBlockedReason
{
    /// <summary>The manifest declares <c>gpu: required</c>, which no container this engine creates can satisfy.</summary>
    GpuNotSupported = 0,

    /// <summary>The runtime does not offer something the manifest's <c>requires</c> list names.</summary>
    RuntimeIncompatible = 1,

    /// <summary>There is no usable container runtime right now.</summary>
    RuntimeUnavailable = 2,

    /// <summary>Less free memory than the manifest's minimum plus the engine's headroom.</summary>
    InsufficientMemory = 3,

    /// <summary>Less free disk on the instance volume than an install needs.</summary>
    InsufficientDisk = 4,

    /// <summary>This application already has an instance; V1 allows exactly one.</summary>
    AlreadyInstalled = 5,

    /// <summary>The installed application is no longer in the catalog, so there is no target manifest to update to.</summary>
    CatalogMissing = 6,

    /// <summary>
    ///     The manifest reads the container bridge — <c>${XE_BRIDGE_ENDPOINT}</c> or <c>${XE_BRIDGE_TOKEN}</c> — and
    ///     this node did not open one, so the deployment cannot be planned at all.
    ///     <para>
    ///         Both previews report it, and both commands refuse on it. The planner raises the same refusal, but it
    ///         raises it inside the pipeline: on an install that is after the row exists, and on an update it would
    ///         be after a version that was working had been stopped.
    ///     </para>
    /// </summary>
    BridgeUnavailable = 7
}

/// <summary>
///     One container port of one service as it is actually published on the host. Always loopback, so the host
///     interface is not carried: a member that is always <c>127.0.0.1</c> is a member two readers read two ways.
/// </summary>
public sealed record ExternalAppPublishedPort(string Service, int ContainerPort, int HostPort);

/// <summary>
///     Reads and writes the <c>PublishedPortsJson</c> column. A JSON OBJECT keyed <c>"&lt;service&gt;:&lt;containerPort&gt;"</c>
///     rather than an array, because the store seeds a new row with <c>{}</c> and an empty array would not parse.
/// </summary>
public static class ExternalAppPublishedPorts
{
    /// <summary>The value the store seeds and the value an instance with nothing published carries.</summary>
    public const string Empty = "{}";

    /// <summary>Serialises the observed bindings. Ordinal key order, so two equal port sets serialise identically.</summary>
    public static string Serialize(IReadOnlyList<ExternalAppPublishedPort> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        var map = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var port in ports)
        {
            map[Key(port.Service, port.ContainerPort)] = port.HostPort;
        }

        return JsonSerializer.Serialize(map, ExternalAppJson.Options);
    }

    /// <summary>
    ///     Parses a stored value back. A malformed or absent document yields an empty list rather than throwing: the
    ///     column is read on every detail render, and a row written by an older build must not make the instance
    ///     unreadable.
    /// </summary>
    public static IReadOnlyList<ExternalAppPublishedPort> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        SortedDictionary<string, int>? map;
        try
        {
            map = JsonSerializer.Deserialize<SortedDictionary<string, int>>(json, ExternalAppJson.Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (map is null)
        {
            return [];
        }

        var ports = new List<ExternalAppPublishedPort>(map.Count);
        foreach (var entry in map)
        {
            var separator = entry.Key.LastIndexOf(':');
            if (separator <= 0
                || !int.TryParse(entry.Key.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort))
            {
                continue;
            }

            ports.Add(new ExternalAppPublishedPort(entry.Key[..separator], containerPort, entry.Value));
        }

        return ports;
    }

    private static string Key(string service, int containerPort)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{service}:{containerPort}");
    }
}

/// <summary>
///     The one serializer configuration every External Apps JSON column is written and read with. One object rather
///     than a literal at each call site: a snapshot written with one set of options and read with another is a
///     manifest that round-trips differently on the way back in.
/// </summary>
public static class ExternalAppJson
{
    /// <summary>Web defaults — camelCase property names, case-insensitive reads — matching the catalog document's own.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>
///     One installed application as a list renders it. The three catalog-derived members
///     (<see cref="UpdateAvailable" />, <see cref="AvailableManifestVersion" />, <see cref="CatalogMissing" />) are
///     computed against the catalog at read time and are never persisted.
/// </summary>
public sealed record ExternalAppInstanceSummary(
    Guid Id,
    string ApplicationId,
    string DisplayName,
    int ManifestVersion,
    ExternalAppInstanceStatus Status,
    ExternalAppDesiredState DesiredState,
    ExternalAppFailureCategory? FailureCategory,
    string? FailureSummary,
    bool UpdateAvailable,
    int? AvailableManifestVersion,
    bool CatalogMissing,
    long UpdatedAtUtc,
    long Version);

/// <summary>
///     Everything the detail page and the settings tab render. <see cref="Manifest" /> is the SANITISED installed
///     snapshot — asset bodies stripped, secret defaults nulled — never the catalog's current manifest, so the page
///     describes the application that is installed rather than the one that could be.
/// </summary>
/// <remarks>
///     <see cref="MaskedVariables" /> carries the stored values with every <c>secret</c> replaced by
///     <see cref="ExternalAppVariableMask.Value" />, which is why this record may print: nothing on it is a secret.
/// </remarks>
public sealed record ExternalAppInstanceDetail(
    ExternalAppInstanceSummary Summary,
    ApplicationManifest Manifest,
    string? TestedVersion,
    IReadOnlyDictionary<string, string> MaskedVariables,
    IReadOnlyList<ExternalAppPublishedPort> PublishedPorts,
    string RuntimeProvider,
    string? RuntimeOverride,
    string StoragePath,
    long LastSequence,
    bool NeedsRecreate,
    long InstalledAtUtc,
    long? StartedAtUtc,
    long? StoppedAtUtc);

/// <summary>
///     What an install would do, evaluated without writing anything: the same runtime, capability, GPU and resource
///     checks admission makes, plus the whole permission disclosure and every declared variable.
/// </summary>
/// <remarks>
///     <see cref="ManifestVersion" /> and <see cref="ManifestSha256" /> fingerprint the manifest as served. The
///     install command carries them back and admission refuses a mismatch, so a catalog refresh between the
///     disclosure and the submit cannot authorise different images.
/// </remarks>
public sealed record InstallPreview(
    string ApplicationId,
    int ManifestVersion,
    string ManifestSha256,
    bool CanInstall,
    ExternalAppBlockedReason? BlockedReason,
    Guid? ExistingInstanceId,
    ApplicationPermissions Permissions,
    ExternalAppEffectivePermissions EffectivePermissions,
    IReadOnlyList<ApplicationVariable> Variables,
    ExternalAppResourceVerdict Resources,
    ContainerRuntimeResolution Runtime,
    IReadOnlyList<string> MissingCapabilities);

/// <summary>
///     What an update would do. <see cref="Variables" /> are the TARGET manifest's definitions and
///     <see cref="CurrentValues" /> the stored values with secrets masked, so a target that declares a newly required
///     variable can be filled in before the instance is stopped.
/// </summary>
public sealed record UpdatePreview(
    string ApplicationId,
    Guid InstanceId,
    int CurrentManifestVersion,
    int TargetManifestVersion,
    string ManifestSha256,
    IReadOnlyList<ApplicationVariable> Variables,
    IReadOnlyDictionary<string, string> CurrentValues,
    IReadOnlyList<string> AddedPermissions,
    ExternalAppEffectivePermissions EffectivePermissions,
    ExternalAppResourceVerdict ResourceVerdict,
    bool CanUpdate,
    ExternalAppBlockedReason? BlockedReason);

/// <summary>
///     An install request. <see cref="AcceptPermissions" /> is never inferred: the server refuses rather than
///     deciding that a caller who sent variables must have read the disclosure.
/// </summary>
public sealed record InstallCommand(
    string ApplicationId,
    string? DisplayName,
    int ManifestVersion,
    string ManifestSha256,
    IReadOnlyDictionary<string, string> Variables,
    bool AcceptPermissions)
{
    // Variables carry the application's admin password and its API keys, so the generated printer would put them
    // into any log line that formats a command. Same shape and same suppressions as the persistence layer's
    // carriers: on a sealed record whose base is object the compiler expects exactly this signature, and every fix
    // the four analyzers suggest changes it into one the compiler no longer recognises as the record's printer.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>
///     An update request. <see cref="Variables" /> are the TARGET manifest's values — a target can declare a
///     variable no stored map holds, and the settings form that would supply it is driven by the old snapshot.
/// </summary>
public sealed record UpdateCommand(
    int ManifestVersion,
    string ManifestSha256,
    bool AcceptPermissions,
    IReadOnlyDictionary<string, string> Variables)
{
#pragma warning disable CA1822, S2325, S1172, IDE0060 // Suppressed printer, same rationale as the record above.
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}
