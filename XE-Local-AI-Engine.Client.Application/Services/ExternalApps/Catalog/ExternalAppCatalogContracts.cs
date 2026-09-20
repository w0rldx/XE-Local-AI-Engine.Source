namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>The curated External Apps catalog document (schema v1): the one JSON document listing the installable containerised applications.</summary>
/// <remarks>
///     Bundled as an embedded resource and optionally replaced by a remote refresh
///     (<see cref="ExternalAppCatalogOptions.RefreshUrl" />). The engine never parses Compose: the catalog converter
///     produces this document offline and it is the only input the runtime understands.
/// </remarks>
public sealed record ExternalAppCatalogDocument(
    int SchemaVersion,
    string GeneratedAtUtc,
    IReadOnlyList<ApplicationManifest> Applications);

/// <summary>One installable application: identity, metadata, declared permissions and resources, its services and its variables.</summary>
/// <remarks>
///     <see cref="ManifestVersion" /> is the version the update flow compares; <see cref="ManifestSha256" />
///     (<see cref="ExternalAppManifestFingerprint" />) fingerprints the manifest as served, so an install or update
///     is rejected once the catalog moved under the user. There is deliberately no <c>user</c> member at either
///     level: a container starts as the image's default user and the engine never passes <c>--user</c>; the boundary
///     is the container, its dropped capabilities, seccomp, <c>no-new-privileges</c> and the loopback-only network.
/// </remarks>
public sealed record ApplicationManifest(
    string Id,
    int ManifestVersion,
    string ManifestSha256,
    string DisplayName,
    string Summary,
    string Description,
    string Homepage,
    string License,
    string Trust,
    string TestedVersion,
    IReadOnlyList<string> Requires,
    ApplicationPermissions Permissions,
    ApplicationResources Resources,
    IReadOnlyList<ApplicationService> Services,
    IReadOnlyList<ApplicationVariable> Variables)
{
    /// <summary>Suppresses the record's generated <c>ToString()</c>, which would print the whole authored catalog.</summary>
    /// <remarks>
    ///     Variable defaults, inlined file bodies and the raw fetched document would reach the node log through one
    ///     downstream structured log of a snapshot; <c>ExternalAppCatalogRecordPrintingTests</c> asserts no manifest
    ///     content can appear. On a sealed record whose base is <c>object</c> the compiler recognises only
    ///     <c>private bool PrintMembers(StringBuilder)</c>, never <c>protected override</c>, which is why CA1822,
    ///     S2325, S1172 and IDE0060 are suppressed: every fix they offer restores the printing <c>ToString()</c>.
    /// </remarks>
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>
///     One container of an application. <see cref="Name" /> is the DNS alias on the instance network;
///     <see cref="Image" /> is always digest-pinned; <see cref="Environment" /> values may carry
///     <c>${NAME}</c> tokens resolved against the declared variables and the built-ins.
/// </summary>
public sealed record ApplicationService(
    string Name,
    string Image,
    string ImageTag,
    IReadOnlyList<string>? Entrypoint,
    IReadOnlyList<string>? Command,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<ApplicationPort> Ports,
    IReadOnlyList<ApplicationStorage> Storage,
    IReadOnlyList<ApplicationFile> Files,
    ApplicationHealthcheck? Healthcheck,
    IReadOnlyList<ApplicationDependency> DependsOn,
    IReadOnlyList<string> CapAdd,
    IReadOnlyList<string> ExtraHosts,
    bool ReadOnlyRootFilesystem);

/// <summary>A container port published to the host; only role <c>ui</c> exists in V1 and every publication is bound to 127.0.0.1.</summary>
/// <remarks>
///     <see cref="OpenPath" /> is non-<see langword="null" /> for exactly one port per application — the one the
///     "Open" action targets. A published port with a <see langword="null" /> path stays reachable by the browser
///     but is not offered as an open target.
/// </remarks>
public sealed record ApplicationPort(int ContainerPort, string Role, int? PreferredHostPort, string? OpenPath);

/// <summary>A writable per-instance directory bind-mounted into the container. No Docker named volumes exist in the model.</summary>
public sealed record ApplicationStorage(string Name, string ContainerPath);

/// <summary>
///     A catalog-shipped read-only asset inlined into the catalog document so the online catalog stays a single
///     fetch. <see cref="Sha256" /> is the lowercase-hex hash of the <em>decoded</em>
///     <see cref="ContentBase64" /> bytes, verified when the engine materialises the file.
/// </summary>
public sealed record ApplicationFile(string Source, string ContainerPath, string Sha256, string ContentBase64);

/// <summary>The container healthcheck the daemon runs. A service that declares one must reach <c>healthy</c> before dependants start.</summary>
public sealed record ApplicationHealthcheck(
    IReadOnlyList<string> Test,
    int IntervalSeconds,
    int TimeoutSeconds,
    int Retries,
    int StartPeriodSeconds);

/// <summary>A start-ordering edge: <see cref="Service" /> must reach <see cref="Condition" /> (<c>started</c> or <c>healthy</c>) first.</summary>
public sealed record ApplicationDependency(string Service, string Condition);

/// <summary>
///     The elevated permissions the application declares, shown at install. <see cref="Internet" /> is always
///     <see langword="true" />: V1 enforces no outbound restriction, so a <see langword="false" /> would be an
///     unenforced promise and is rejected by the validator. <see cref="LocalNetwork" /> is disclosure only.
/// </summary>
public sealed record ApplicationPermissions(bool Internet, bool LocalNetwork, string HostFiles, string Gpu);

/// <summary>
///     Admission-gate inputs. The memory figures gate install only — V1 sets no per-container memory or CPU
///     ceiling and <see cref="CpuHint" /> is informational; <see cref="PidsLimit" /> is the one per-container ceiling.
/// </summary>
public sealed record ApplicationResources(int MinimumMemoryMb, int RecommendedMemoryMb, int CpuHint, int PidsLimit);

/// <summary>
///     One user-supplied configuration value. <see cref="Type" /> is <c>string</c>, <c>secret</c>, <c>integer</c>,
///     <c>boolean</c> or <c>enum</c>; a <c>secret</c> never carries a default and is masked in every DTO.
/// </summary>
public sealed record ApplicationVariable(
    string Name,
    string Label,
    string? Description,
    string Type,
    bool Required,
    string? Default,
    IReadOnlyList<string>? AllowedValues,
    ApplicationVariableValidation? Validation,
    bool Advanced);

/// <summary>Input constraints applied to a variable's value at install time. <see cref="Pattern" /> is compiled non-backtracking.</summary>
public sealed record ApplicationVariableValidation(int? MinLength, int? MaxLength, string? Pattern);

/// <summary>
///     Result of <see cref="ExternalAppCatalogValidator.Validate" />. A document with any invalid application is
///     rejected wholesale (<see cref="Document" /> <see langword="null" />, every problem described with its
///     <c>applications[i].field</c> path), so a partially valid catalog can never be served.
/// </summary>
public sealed record ExternalAppCatalogValidationResult
{
    private ExternalAppCatalogValidationResult(bool isValid, ExternalAppCatalogDocument? document, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Document = document;
        Errors = errors;
    }

    /// <summary>Whether the document passed every rule.</summary>
    public bool IsValid { get; }

    /// <summary>The parsed document, non-<see langword="null" /> only when <see cref="IsValid" /> is <see langword="true" />.</summary>
    public ExternalAppCatalogDocument? Document { get; }

    /// <summary>Every rule violation, each prefixed with the path of the node it applies to.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Creates a passing result carrying <paramref name="document" />.</summary>
    public static ExternalAppCatalogValidationResult Success(ExternalAppCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new ExternalAppCatalogValidationResult(isValid: true, document, []);
    }

    /// <summary>Creates a failing result carrying <paramref name="errors" />.</summary>
    public static ExternalAppCatalogValidationResult Failure(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new ExternalAppCatalogValidationResult(isValid: false, document: null, errors);
    }
}

/// <summary>Where a served <see cref="ExternalAppCatalogDocument" /> came from — surfaced on the catalog endpoint.</summary>
public enum ExternalAppCatalogSource
{
    /// <summary>The embedded, in-repo seed catalog (no remote refresh configured, or none has ever succeeded).</summary>
    Bundled = 0,

    /// <summary>Freshly fetched and validated from <see cref="ExternalAppCatalogOptions.RefreshUrl" /> this run.</summary>
    Remote = 1,

    /// <summary>
    ///     A previously fetched remote catalog persisted to disk, served because the latest attempt failed
    ///     (network/validation) — a transient failure never regresses a working catalog to bundled-only.
    /// </summary>
    RemoteLastGood = 2
}

/// <summary>
///     The catalog currently in effect plus its provenance. <see cref="LastRefreshFailure" /> carries the
///     human-readable reason the most recent refresh attempt fell back, so the API can report it without
///     re-attempting a fetch; a successful refresh clears it.
/// </summary>
public sealed record ExternalAppCatalogSnapshot(
    ExternalAppCatalogDocument Document,
    ExternalAppCatalogSource Source,
    DateTimeOffset? FetchedAtUtc,
    string? SourceUrl,
    string? LastRefreshFailure)
{
    /// <summary>Suppresses the record's generated <c>ToString()</c>, which would print the whole authored catalog.</summary>
    /// <remarks>
    ///     Variable defaults, inlined file bodies and the raw fetched document would reach the node log through one
    ///     downstream structured log of a snapshot; <c>ExternalAppCatalogRecordPrintingTests</c> asserts no manifest
    ///     content can appear. On a sealed record whose base is <c>object</c> the compiler recognises only
    ///     <c>private bool PrintMembers(StringBuilder)</c>, never <c>protected override</c>, which is why CA1822,
    ///     S2325, S1172 and IDE0060 are suppressed: every fix they offer restores the printing <c>ToString()</c>.
    /// </remarks>
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>
///     Outcome of an explicit refresh: the snapshot now being served plus the failure message when the fetch or its
///     validation failed and the provider fell back. Both members are <see langword="null" />-free on success
///     (<see cref="FailureMessage" /> is <see langword="null" />).
/// </summary>
public sealed class ExternalAppCatalogRefreshResult
{
    public required ExternalAppCatalogSnapshot Snapshot { get; init; }

    public required string? FailureMessage { get; init; }
}

/// <summary>
///     Computes the canonical fingerprint of an <see cref="ApplicationManifest" /> — the value carried in
///     <see cref="ApplicationManifest.ManifestSha256" /> and echoed back by install/update commands.
/// </summary>
/// <remarks>
///     The canonical form is fixed because two languages compute it — this type and the Python catalog converter —
///     and is written down rather than inferred: see <c>docs/wiki/23-external-apps.md</c> ("The manifest"). Sorting
///     every object's properties ordinal by name at every level is what makes this record's declaration order and
///     the converter's dict order irrelevant; <c>manifestSha256</c> is removed because a document cannot contain its
///     own hash.
/// </remarks>
public static class ExternalAppManifestFingerprint
{
    /// <summary>The JSON property name the fingerprint excludes from its own input.</summary>
    public const string HashPropertyName = "manifestSha256";

    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Returns the lowercase-hex SHA-256 of <paramref name="manifest" />'s canonical JSON form.</summary>
    public static string Compute(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var serialized = JsonSerializer.SerializeToNode(manifest, CanonicalOptions) as JsonObject
                         ?? new JsonObject();
        _ = serialized.Remove(HashPropertyName);

        var canonicalJson = Canonicalize(serialized)!.ToJsonString(CanonicalOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                {
                    var sorted = new JsonObject();
                    foreach (var property in jsonObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        sorted[property.Key] = Canonicalize(property.Value?.DeepClone());
                    }

                    return sorted;
                }

            case JsonArray jsonArray:
                {
                    var ordered = new JsonArray();
                    foreach (var item in jsonArray)
                    {
                        ordered.Add(Canonicalize(item?.DeepClone()));
                    }

                    return ordered;
                }

            default:
                return node?.DeepClone();
        }
    }
}
