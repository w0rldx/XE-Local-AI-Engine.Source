namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     Validates a candidate External Apps catalog document — the schema-version gate plus every content rule a
///     bundled or remotely refreshed catalog must pass before it can replace the served snapshot. Tolerant of parse
///     failures (never throws for malformed input) and strict on content: a document with any invalid application is
///     rejected wholesale rather than silently dropping the bad rows, so a corrupt payload can never partially
///     install. Everything a later slice would otherwise re-check — digest pinning, token resolution, capability
///     allow-listing, acyclic start ordering, file integrity — is decided here, once, at catalog load.
/// </summary>
public static partial class ExternalAppCatalogValidator
{
    /// <summary>The only <see cref="ExternalAppCatalogDocument.SchemaVersion" /> this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Prefix of the built-in token that resolves to a service's published loopback host port.</summary>
    public const string BuiltInUiPortPrefix = "XE_UI_HOST_PORT_";

    /// <summary>Variable-name prefix reserved for engine-supplied built-ins; a catalog may not declare one.</summary>
    public const string ReservedVariablePrefix = "XE_";

    /// <summary>Largest decoded size of a catalog-shipped file body.</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Longest accepted <see cref="ApplicationPort.OpenPath" />; the value only ever names a local route.</summary>
    public const int MaxOpenPathLength = 256;

    /// <summary>
    ///     The container-runtime capability names a manifest's <c>requires[]</c> may name, camelCase on the wire.
    ///     S2 asserts these equal the runtime layer's own capability names, so a drift is a red test rather than a
    ///     runtime "incompatible" answer to a valid manifest.
    /// </summary>
    public static readonly IReadOnlyList<string> CapabilityNames =
    [
        "containers", "networks", "bindStorage", "loopbackPortPublishing", "healthChecks", "restartPolicies",
        "logs", "imagePull", "gpuDevices"
    ];

    /// <summary>
    ///     Docker's own default capability set — the 14 that plain <c>docker run</c> grants. The container policy
    ///     still applies <c>cap_drop ALL</c> and adds back only what a manifest lists, so a service can never exceed
    ///     an unhardened <c>docker run</c>; anything outside this table is a validation error.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedCapAdd =
    [
        "AUDIT_WRITE", "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "MKNOD", "NET_BIND_SERVICE", "NET_RAW",
        "SETFCAP", "SETGID", "SETPCAP", "SETUID", "SYS_CHROOT"
    ];

    /// <summary>Engine-supplied tokens a manifest environment value may reference without declaring a variable.</summary>
    public static readonly IReadOnlyList<string> BuiltInVariableNames =
        ["XE_UID", "XE_GID", "XE_INSTANCE_ID", "XE_BRIDGE_ENDPOINT", "XE_BRIDGE_TOKEN"];

    /// <summary>Accepted <see cref="ApplicationVariable.Type" /> values.</summary>
    public static readonly IReadOnlyList<string> VariableTypes = ["string", "secret", "integer", "boolean", "enum"];

    /// <summary>Accepted <see cref="ApplicationPermissions.HostFiles" /> values.</summary>
    public static readonly IReadOnlyList<string> HostFilesValues = ["none", "readOnly", "readWrite"];

    /// <summary>Accepted <see cref="ApplicationPermissions.Gpu" /> values. <c>required</c> parses but fails install.</summary>
    public static readonly IReadOnlyList<string> GpuValues = ["none", "optional", "required"];

    /// <summary>Accepted <see cref="ApplicationDependency.Condition" /> values.</summary>
    public static readonly IReadOnlyList<string> DependsOnConditions = ["started", "healthy"];

    /// <summary>Accepted <see cref="ApplicationPort.Role" /> values; only a UI port is ever published.</summary>
    public static readonly IReadOnlyList<string> PortRoles = ["ui"];

    /// <summary>Accepted <see cref="ApplicationService.ExtraHosts" /> entries.</summary>
    public static readonly IReadOnlyList<string> ExtraHostValues = ["host-gateway"];

    /// <summary>Service names the engine reserves for itself.</summary>
    public static readonly IReadOnlyList<string> ReservedServiceNames = ["xe"];

    /// <summary>The capability a manifest must require before any service publishes a port.</summary>
    private const string LoopbackPortPublishingCapability = "loopbackPortPublishing";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan PatternCompileTimeout = TimeSpan.FromSeconds(seconds: 1);

    private static readonly IReadOnlySet<string> CapabilityNameSet = ToOrdinalSet(CapabilityNames);
    private static readonly IReadOnlySet<string> AllowedCapAddSet = ToOrdinalSet(AllowedCapAdd);
    private static readonly IReadOnlySet<string> BuiltInVariableNameSet = ToOrdinalSet(BuiltInVariableNames);
    private static readonly IReadOnlySet<string> VariableTypeSet = ToOrdinalSet(VariableTypes);
    private static readonly IReadOnlySet<string> HostFilesValueSet = ToOrdinalSet(HostFilesValues);
    private static readonly IReadOnlySet<string> GpuValueSet = ToOrdinalSet(GpuValues);
    private static readonly IReadOnlySet<string> DependsOnConditionSet = ToOrdinalSet(DependsOnConditions);
    private static readonly IReadOnlySet<string> PortRoleSet = ToOrdinalSet(PortRoles);
    private static readonly IReadOnlySet<string> ExtraHostValueSet = ToOrdinalSet(ExtraHostValues);
    private static readonly IReadOnlySet<string> ReservedServiceNameSet = ToOrdinalSet(ReservedServiceNames);

    /// <summary>
    ///     Whether an image reference is pinned as <c>&lt;reference&gt;@sha256:&lt;64 lowercase hex digits&gt;</c>.
    ///     Public because the rule has a second enforcement point — the operator-configured storage helper image —
    ///     and two copies of this pattern would be two rules that could drift apart.
    /// </summary>
    public static bool IsDigestPinnedImage(string? image)
    {
        return image is not null && DigestPinnedImageRegex().IsMatch(image);
    }

    /// <summary>Parses and validates <paramref name="rawJson" />. Never throws — a parse failure is a validation failure.</summary>
    public static ExternalAppCatalogValidationResult Validate(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return ExternalAppCatalogValidationResult.Failure(["Catalog JSON is empty."]);
        }

        ExternalAppCatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ExternalAppCatalogDocument>(rawJson, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return ExternalAppCatalogValidationResult.Failure([$"Catalog JSON could not be parsed: {exception.Message}"]);
        }

        if (document is null)
        {
            return ExternalAppCatalogValidationResult.Failure(["Catalog JSON deserialized to null."]);
        }

        var errors = new List<string>();
        ValidateDocument(document, errors);

        return errors.Count == 0
            ? ExternalAppCatalogValidationResult.Success(document)
            : ExternalAppCatalogValidationResult.Failure(errors);
    }

    private static void ValidateDocument(ExternalAppCatalogDocument document, List<string> errors)
    {
        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add($"Unsupported schemaVersion {document.SchemaVersion.ToString(CultureInfo.InvariantCulture)} (expected {SupportedSchemaVersion.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (string.IsNullOrWhiteSpace(document.GeneratedAtUtc))
        {
            errors.Add("generatedAtUtc is required.");
        }
        else if (!IsIso8601Utc(document.GeneratedAtUtc))
        {
            errors.Add("generatedAtUtc must be an ISO-8601 UTC timestamp.");
        }

        if (document.Applications is null)
        {
            errors.Add("applications array is required.");
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Applications.Count; index++)
        {
            ValidateApplication(document.Applications[index], index, seenIds, errors);
        }
    }

    private static void ValidateApplication(
        ApplicationManifest? manifest,
        int index,
        HashSet<string> seenIds,
        List<string> errors)
    {
        var prefix = $"applications[{index.ToString(CultureInfo.InvariantCulture)}]";

        if (manifest is null)
        {
            errors.Add($"{prefix} is required.");
            return;
        }

        ValidateApplicationIdentity(manifest, prefix, seenIds, errors);
        ValidateApplicationMetadata(manifest, prefix, errors);
        ValidateApplicationRequires(manifest, prefix, errors);
        ValidateApplicationPermissions(manifest, prefix, errors);
        ValidateApplicationResources(manifest, prefix, errors);
        ValidateApplicationFingerprint(manifest, prefix, errors);

        if (manifest.Services is null)
        {
            errors.Add($"{prefix}.services is required.");
            return;
        }

        if (manifest.Variables is null)
        {
            errors.Add($"{prefix}.variables is required.");
            return;
        }

        var context = ApplicationValidationContext.Create(manifest);
        ValidateServiceNames(manifest, prefix, errors);
        ValidateVariableNames(manifest, prefix, errors);

        if (manifest.Services.Count is 0 or > 8)
        {
            errors.Add($"{prefix}.services must contain between 1 and 8 services.");
        }

        for (var serviceIndex = 0; serviceIndex < manifest.Services.Count; serviceIndex++)
        {
            ValidateService(manifest.Services[serviceIndex], serviceIndex, prefix, context, errors);
        }

        ValidateDependencyCycles(manifest, prefix, context, errors);

        for (var variableIndex = 0; variableIndex < manifest.Variables.Count; variableIndex++)
        {
            ValidateVariable(manifest.Variables[variableIndex], variableIndex, prefix, context, errors);
        }

        ValidateApplicationCrossRules(manifest, prefix, context, errors);
    }

    private static void ValidateApplicationIdentity(
        ApplicationManifest manifest,
        string prefix,
        HashSet<string> seenIds,
        List<string> errors)
    {
        if (string.IsNullOrEmpty(manifest.Id) || !ApplicationIdRegex().IsMatch(manifest.Id))
        {
            errors.Add($"{prefix}.id must match ^[a-z][a-z0-9-]{{1,40}}$.");
        }
        else if (!seenIds.Add(manifest.Id))
        {
            errors.Add($"{prefix}.id '{manifest.Id}' is a duplicate.");
        }

        if (manifest.ManifestVersion < 1)
        {
            errors.Add($"{prefix}.manifestVersion must be 1 or greater.");
        }
    }

    private static void ValidateApplicationMetadata(ApplicationManifest manifest, string prefix, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 80)
        {
            errors.Add($"{prefix}.displayName is required and must be at most 80 characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Summary) || manifest.Summary.Length > 200)
        {
            errors.Add($"{prefix}.summary is required and must be at most 200 characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Description) || manifest.Description.Length > 2048)
        {
            errors.Add($"{prefix}.description is required and must be at most 2048 characters.");
        }

        if (!Uri.TryCreate(manifest.Homepage, UriKind.Absolute, out var homepage)
            || !string.Equals(homepage.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.homepage must be an absolute https URL.");
        }

        if (string.IsNullOrWhiteSpace(manifest.License))
        {
            errors.Add($"{prefix}.license is required.");
        }

        if (!string.Equals(manifest.Trust, "xeCatalog", StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.trust must be 'xeCatalog'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TestedVersion))
        {
            errors.Add($"{prefix}.testedVersion is required.");
        }
    }

    private static void ValidateApplicationRequires(ApplicationManifest manifest, string prefix, List<string> errors)
    {
        if (manifest.Requires is null)
        {
            errors.Add($"{prefix}.requires is required.");
            return;
        }

        // The set is built up front, never with a `distinct.Add(...)` side effect inside Enumerable.All: All
        // short-circuits on the first false, so the set would be incomplete and the duplicate check would depend on
        // where the first other failure happened to sit. A short count catches both a null entry and a duplicate.
        var distinct = new HashSet<string>(manifest.Requires.OfType<string>(), StringComparer.Ordinal);
        var valid = manifest.Requires.Count > 0
                    && distinct.Count == manifest.Requires.Count
                    && distinct.All(CapabilityNameSet.Contains);

        if (!valid)
        {
            errors.Add($"{prefix}.requires must be a non-empty distinct subset of {string.Join(", ", CapabilityNames)}.");
        }
    }

    private static void ValidateApplicationPermissions(ApplicationManifest manifest, string prefix, List<string> errors)
    {
        if (manifest.Permissions is null)
        {
            errors.Add($"{prefix}.permissions is required.");
            return;
        }

        if (manifest.Permissions.HostFiles is null || !HostFilesValueSet.Contains(manifest.Permissions.HostFiles))
        {
            errors.Add($"{prefix}.permissions.hostFiles must be one of {string.Join(", ", HostFilesValues)}.");
        }

        if (manifest.Permissions.Gpu is null || !GpuValueSet.Contains(manifest.Permissions.Gpu))
        {
            errors.Add($"{prefix}.permissions.gpu must be one of {string.Join(", ", GpuValues)}.");
        }

        if (!manifest.Permissions.Internet)
        {
            errors.Add($"{prefix}.permissions.internet must be true: outbound restriction is not supported in this version.");
        }
    }

    private static void ValidateApplicationResources(ApplicationManifest manifest, string prefix, List<string> errors)
    {
        if (manifest.Resources is null)
        {
            errors.Add($"{prefix}.resources is required.");
            return;
        }

        if (manifest.Resources.MinimumMemoryMb < 256)
        {
            errors.Add($"{prefix}.resources.minimumMemoryMb must be at least 256.");
        }

        if (manifest.Resources.RecommendedMemoryMb < manifest.Resources.MinimumMemoryMb)
        {
            errors.Add($"{prefix}.resources.recommendedMemoryMb must be at least minimumMemoryMb.");
        }

        if (manifest.Resources.CpuHint < 1)
        {
            errors.Add($"{prefix}.resources.cpuHint must be at least 1.");
        }

        if (manifest.Resources.PidsLimit < 64)
        {
            errors.Add($"{prefix}.resources.pidsLimit must be at least 64.");
        }
    }

    private static void ValidateApplicationFingerprint(ApplicationManifest manifest, string prefix, List<string> errors)
    {
        if (manifest.ManifestSha256 is null || !Sha256HexRegex().IsMatch(manifest.ManifestSha256))
        {
            errors.Add($"{prefix}.manifestSha256 must be 64 lowercase hex digits.");
            return;
        }

        if (!string.Equals(manifest.ManifestSha256, ExternalAppManifestFingerprint.Compute(manifest), StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.manifestSha256 does not match the canonical manifest fingerprint.");
        }
    }

    private static void ValidateServiceNames(ApplicationManifest manifest, string prefix, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Services.Count; index++)
        {
            var name = manifest.Services[index]?.Name;
            if (name is null)
            {
                continue;
            }

            var path = $"{prefix}.services[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (!seen.Add(name))
            {
                errors.Add($"{path}.name '{name}' is a duplicate.");
            }

            if (ReservedServiceNameSet.Contains(name))
            {
                errors.Add($"{path}.name '{name}' is reserved.");
            }
        }
    }

    private static void ValidateVariableNames(ApplicationManifest manifest, string prefix, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Variables.Count; index++)
        {
            var name = manifest.Variables[index]?.Name;
            if (name is null)
            {
                continue;
            }

            var path = $"{prefix}.variables[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (!seen.Add(name))
            {
                errors.Add($"{path}.name '{name}' is a duplicate.");
            }

            if (name.StartsWith(ReservedVariablePrefix, StringComparison.Ordinal))
            {
                errors.Add($"{path}.name '{name}' must not start with the reserved prefix {ReservedVariablePrefix}.");
            }
        }
    }

    private static void ValidateApplicationCrossRules(
        ApplicationManifest manifest,
        string prefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        if (context.OpenPathCount != 1)
        {
            errors.Add($"{prefix} must declare exactly one port with an openPath (found {context.OpenPathCount.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (context.DeclaresExtraHosts && manifest.Permissions?.LocalNetwork is not true)
        {
            errors.Add($"{prefix}.permissions.localNetwork must be true when a service declares extraHosts.");
        }

        if (context.DeclaresPort
            && manifest.Requires is not null
            && !manifest.Requires.Contains(LoopbackPortPublishingCapability, StringComparer.Ordinal))
        {
            errors.Add($"{prefix}.requires must contain '{LoopbackPortPublishingCapability}' when a service publishes a port.");
        }
    }

    private static void ValidateService(
        ApplicationService? service,
        int index,
        string applicationPrefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        var prefix = $"{applicationPrefix}.services[{index.ToString(CultureInfo.InvariantCulture)}]";

        if (service is null)
        {
            errors.Add($"{prefix} is required.");
            return;
        }

        if (service.Name is null || !ServiceNameRegex().IsMatch(service.Name))
        {
            errors.Add($"{prefix}.name must match ^[a-z][a-z0-9-]{{0,30}}$.");
        }

        if (!IsDigestPinnedImage(service.Image))
        {
            errors.Add($"{prefix}.image must be pinned as '<reference>@sha256:<64 hex digits>'.");
        }

        if (string.IsNullOrWhiteSpace(service.ImageTag) || string.Equals(service.ImageTag, "latest", StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.imageTag is required and must not be 'latest'.");
        }

        ValidateArgumentVector(service.Entrypoint, $"{prefix}.entrypoint", errors);
        ValidateArgumentVector(service.Command, $"{prefix}.command", errors);
        ValidateServiceEnvironment(service, prefix, context, errors);
        ValidateServicePorts(service, prefix, context, errors);
        ValidateServiceStorage(service, prefix, errors);
        ValidateServiceFiles(service, prefix, errors);
        ValidateServiceHealthcheck(service, prefix, errors);
        ValidateServiceDependencies(service, prefix, context, errors);
        ValidateServiceCapabilities(service, prefix, errors);
        ValidateServiceExtraHosts(service, prefix, errors);
    }

    private static void ValidateArgumentVector(IReadOnlyList<string>? vector, string path, List<string> errors)
    {
        if (vector is null)
        {
            return;
        }

        if (vector.Count == 0 || vector.Any(string.IsNullOrEmpty))
        {
            errors.Add($"{path} must be null or a non-empty array of non-empty strings.");
        }
    }

    private static void ValidateServiceEnvironment(
        ApplicationService service,
        string prefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        if (service.Environment is null)
        {
            errors.Add($"{prefix}.environment is required.");
            return;
        }

        foreach (var (key, value) in service.Environment)
        {
            if (key is null || !IdentifierRegex().IsMatch(key))
            {
                errors.Add($"{prefix}.environment key '{key}' is not a valid environment-variable name.");
                continue;
            }

            if (value is null)
            {
                errors.Add($"{prefix}.environment['{key}'] must not be null.");
                continue;
            }

            var tokens = new List<string>();
            if (!TryReadTokens(value, tokens))
            {
                errors.Add($"{prefix}.environment['{key}'] contains a malformed '${{' token.");
                continue;
            }

            foreach (var token in tokens)
            {
                ResolveToken(token, key, prefix, context, errors);
            }
        }
    }

    private static void ResolveToken(
        string token,
        string key,
        string prefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        if (token.StartsWith(BuiltInUiPortPrefix, StringComparison.Ordinal))
        {
            var serviceName = token[BuiltInUiPortPrefix.Length..];
            if (!context.ServicesWithUiPort.Contains(serviceName))
            {
                errors.Add($"{prefix}.environment['{key}'] references '${{{token}}}' but service '{serviceName}' publishes no ui port.");
            }

            return;
        }

        if (BuiltInVariableNameSet.Contains(token))
        {
            return;
        }

        if (context.DeclaredVariableNames.Contains(token))
        {
            _ = context.ReferencedVariableNames.Add(token);
            return;
        }

        errors.Add($"{prefix}.environment['{key}'] references undeclared variable '${{{token}}}'.");
    }

    private static bool TryReadTokens(string value, List<string> tokens)
    {
        var index = 0;
        while (index < value.Length)
        {
            var dollar = value.IndexOf('$', index);
            if (dollar < 0)
            {
                return true;
            }

            if (dollar + 1 >= value.Length || value[dollar + 1] != '{')
            {
                index = dollar + 1;
                continue;
            }

            var match = SubstitutionTokenRegex().Match(value, dollar);
            if (!match.Success || match.Index != dollar)
            {
                return false;
            }

            tokens.Add(match.Groups["name"].Value);
            index = dollar + match.Length;
        }

        return true;
    }

    private static void ValidateServicePorts(
        ApplicationService service,
        string prefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        if (service.Ports is null)
        {
            errors.Add($"{prefix}.ports is required.");
            return;
        }

        var seenContainerPorts = new HashSet<int>();
        for (var index = 0; index < service.Ports.Count; index++)
        {
            var path = $"{prefix}.ports[{index.ToString(CultureInfo.InvariantCulture)}]";
            var port = service.Ports[index];
            if (port is null)
            {
                errors.Add($"{path} is required.");
                continue;
            }

            if (port.ContainerPort is < 1 or > 65535)
            {
                errors.Add($"{path}.containerPort must be between 1 and 65535.");
            }
            else if (!seenContainerPorts.Add(port.ContainerPort))
            {
                errors.Add($"{path}.containerPort {port.ContainerPort.ToString(CultureInfo.InvariantCulture)} is a duplicate.");
            }

            if (port.Role is null || !PortRoleSet.Contains(port.Role))
            {
                errors.Add($"{path}.role must be 'ui'.");
            }

            if (port.PreferredHostPort is { } preferred)
            {
                if (preferred is < 1024 or > 65535)
                {
                    errors.Add($"{path}.preferredHostPort must be between 1024 and 65535.");
                }
                else if (!context.SeenPreferredHostPorts.Add(preferred))
                {
                    errors.Add($"{path}.preferredHostPort {preferred.ToString(CultureInfo.InvariantCulture)} is a duplicate.");
                }
            }

            if (port.OpenPath is { } openPath && !IsAcceptableOpenPath(openPath))
            {
                errors.Add($"{path}.openPath must be an absolute path of at most {MaxOpenPathLength.ToString(CultureInfo.InvariantCulture)} characters, without a second leading '/', a backslash or a control character.");
            }
        }
    }

    private static void ValidateServiceStorage(ApplicationService service, string prefix, List<string> errors)
    {
        if (service.Storage is null)
        {
            errors.Add($"{prefix}.storage is required.");
            return;
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var acceptedPaths = new List<string>();
        for (var index = 0; index < service.Storage.Count; index++)
        {
            var path = $"{prefix}.storage[{index.ToString(CultureInfo.InvariantCulture)}]";
            var storage = service.Storage[index];
            if (storage is null)
            {
                errors.Add($"{path} is required.");
                continue;
            }

            if (storage.Name is null || !ServiceNameRegex().IsMatch(storage.Name))
            {
                errors.Add($"{path}.name must match ^[a-z][a-z0-9-]{{0,30}}$.");
            }
            else if (!seenNames.Add(storage.Name))
            {
                errors.Add($"{path}.name '{storage.Name}' is a duplicate.");
            }

            if (!IsMountablePath(storage.ContainerPath))
            {
                errors.Add($"{path}.containerPath must be an absolute POSIX path without '..', '.' or empty segments.");
                continue;
            }

            var existing = acceptedPaths.Find(other => IsSameOrNested(storage.ContainerPath, other));
            if (existing is not null)
            {
                errors.Add(OverlapError(path, "containerPath", storage.ContainerPath, existing));
                continue;
            }

            acceptedPaths.Add(storage.ContainerPath);
        }
    }

    private static void ValidateServiceFiles(ApplicationService service, string prefix, List<string> errors)
    {
        if (service.Files is null)
        {
            errors.Add($"{prefix}.files is required.");
            return;
        }

        var storagePaths = service.Storage?
                               .Where(storage => storage is not null && IsMountablePath(storage.ContainerPath))
                               .Select(storage => storage.ContainerPath)
                               .ToList()
                           ?? [];

        var acceptedContainerPaths = new List<string>();
        var acceptedSources = new List<string>();
        for (var index = 0; index < service.Files.Count; index++)
        {
            var path = $"{prefix}.files[{index.ToString(CultureInfo.InvariantCulture)}]";
            var file = service.Files[index];
            if (file is null)
            {
                errors.Add($"{path} is required.");
                continue;
            }

            if (!IsAcceptableFileSource(file.Source))
            {
                errors.Add($"{path}.source must be a relative POSIX path without '..', '.' or empty segments.");
            }
            else
            {
                // The installer materialises every entry at {instanceDir}/files/{service}/{source}, so two entries
                // sharing a source collide on one host file: the later body wins and the earlier containerPath is
                // silently fed the wrong content. Nesting is the same collision with a directory in the way — every
                // entry is a regular file, so 'config' and 'config/child' cannot both be materialised.
                var clash = acceptedSources.Find(other => IsSameOrNested(file.Source, other));
                if (clash is null)
                {
                    acceptedSources.Add(file.Source);
                }
                else
                {
                    errors.Add(OverlapError(path, "source", file.Source, clash));
                }
            }

            if (!IsMountablePath(file.ContainerPath))
            {
                errors.Add($"{path}.containerPath must be an absolute POSIX path without '..', '.' or empty segments.");
            }
            else
            {
                var mount = storagePaths.Find(storagePath => IsSameOrNested(file.ContainerPath, storagePath));
                if (mount is not null)
                {
                    errors.Add($"{path}.containerPath must not sit inside storage mount '{mount}'.");
                }
                else
                {
                    // Two entries writing the same path: whichever materialises last silently wins, so the installed
                    // file would depend on array order rather than on anything the author declared. A target nesting
                    // inside another is the same defect: both entries are regular files, so the container cannot
                    // bind '/etc/config' and '/etc/config/child' at once.
                    var clash = acceptedContainerPaths.Find(other => IsSameOrNested(file.ContainerPath, other));
                    if (clash is null)
                    {
                        acceptedContainerPaths.Add(file.ContainerPath);
                    }
                    else
                    {
                        errors.Add(OverlapError(path, "containerPath", file.ContainerPath, clash));
                    }
                }
            }

            ValidateFileBody(file, path, errors);
        }
    }

    private static void ValidateFileBody(ApplicationFile file, string path, List<string> errors)
    {
        if (file.Sha256 is null || !Sha256HexRegex().IsMatch(file.Sha256))
        {
            errors.Add($"{path}.sha256 must be 64 lowercase hex digits.");
        }

        if (file.ContentBase64 is null)
        {
            // The contract types the body as a non-nullable string, so a missing or null member survives
            // deserialization as a null the rest of the engine will dereference at materialisation. Coercing it to
            // the empty string here would instead let a body-less entry validate against the sha256 of zero bytes.
            errors.Add($"{path}.contentBase64 is required (use \"\" for an empty file).");
            return;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(file.ContentBase64);
        }
        catch (FormatException)
        {
            errors.Add($"{path}.contentBase64 is not valid base64.");
            return;
        }

        if (decoded.Length > MaxFileBytes)
        {
            errors.Add($"{path}.contentBase64 exceeds the {MaxFileBytes.ToString(CultureInfo.InvariantCulture)}-byte limit.");
            return;
        }

        if (file.Sha256 is not null
            && Sha256HexRegex().IsMatch(file.Sha256)
            && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(decoded)), file.Sha256, StringComparison.Ordinal))
        {
            errors.Add($"{path}.contentBase64 does not match the declared sha256.");
        }
    }

    private static void ValidateServiceHealthcheck(ApplicationService service, string prefix, List<string> errors)
    {
        if (service.Healthcheck is not { } healthcheck)
        {
            return;
        }

        var path = $"{prefix}.healthcheck";
        if (healthcheck.Test is null || healthcheck.Test.Count < 2 || healthcheck.Test.Any(string.IsNullOrEmpty))
        {
            errors.Add($"{path}.test must contain at least two non-empty elements.");
        }
        else if (healthcheck.Test[0] is not ("CMD" or "CMD-SHELL"))
        {
            errors.Add($"{path}.test must start with 'CMD' or 'CMD-SHELL'.");
        }

        if (healthcheck.IntervalSeconds is < 1 or > 300)
        {
            errors.Add($"{path}.intervalSeconds must be between 1 and 300.");
        }

        if (healthcheck.TimeoutSeconds is < 1 or > 120)
        {
            errors.Add($"{path}.timeoutSeconds must be between 1 and 120.");
        }

        if (healthcheck.Retries is < 1 or > 100)
        {
            errors.Add($"{path}.retries must be between 1 and 100.");
        }

        if (healthcheck.StartPeriodSeconds is < 0 or > 600)
        {
            errors.Add($"{path}.startPeriodSeconds must be between 0 and 600.");
        }
    }

    private static void ValidateServiceDependencies(
        ApplicationService service,
        string prefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        if (service.DependsOn is null)
        {
            errors.Add($"{prefix}.dependsOn is required.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < service.DependsOn.Count; index++)
        {
            var path = $"{prefix}.dependsOn[{index.ToString(CultureInfo.InvariantCulture)}]";
            var dependency = service.DependsOn[index];
            if (dependency is null)
            {
                errors.Add($"{path} is required.");
                continue;
            }

            var target = dependency.Service;
            if (target is null || !context.ServiceNames.Contains(target))
            {
                errors.Add($"{path}.service '{target}' is not a declared service.");
            }
            else if (string.Equals(target, service.Name, StringComparison.Ordinal))
            {
                errors.Add($"{path}.service '{target}' must not be the declaring service.");
            }
            else if (!seen.Add(target))
            {
                errors.Add($"{path}.service '{target}' is a duplicate.");
            }

            if (dependency.Condition is null || !DependsOnConditionSet.Contains(dependency.Condition))
            {
                errors.Add($"{path}.condition must be one of {string.Join(", ", DependsOnConditions)}.");
                continue;
            }

            if (string.Equals(dependency.Condition, "healthy", StringComparison.Ordinal)
                && target is not null
                && context.ServiceNames.Contains(target)
                && !context.ServicesWithHealthcheck.Contains(target))
            {
                errors.Add($"{path}.condition 'healthy' requires service '{target}' to declare a healthcheck.");
            }
        }
    }

    private static void ValidateServiceCapabilities(ApplicationService service, string prefix, List<string> errors)
    {
        if (service.CapAdd is null)
        {
            errors.Add($"{prefix}.capAdd is required.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in service.CapAdd)
        {
            if (capability is null || !AllowedCapAddSet.Contains(capability))
            {
                errors.Add($"{prefix}.capAdd contains '{capability}', which is not in the allow-list {string.Join(", ", AllowedCapAdd)}.");
                continue;
            }

            if (!seen.Add(capability))
            {
                errors.Add($"{prefix}.capAdd contains duplicate '{capability}'.");
            }
        }
    }

    private static void ValidateServiceExtraHosts(ApplicationService service, string prefix, List<string> errors)
    {
        if (service.ExtraHosts is null)
        {
            errors.Add($"{prefix}.extraHosts is required.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in service.ExtraHosts)
        {
            if (host is null || !ExtraHostValueSet.Contains(host))
            {
                errors.Add($"{prefix}.extraHosts contains '{host}'; only 'host-gateway' is allowed.");
                continue;
            }

            if (!seen.Add(host))
            {
                errors.Add($"{prefix}.extraHosts contains duplicate '{host}'.");
            }
        }
    }

    private static void ValidateDependencyCycles(
        ApplicationManifest manifest,
        string prefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var service in manifest.Services)
        {
            if (service?.Name is not { } name || !edges.TryAdd(name, []))
            {
                continue;
            }

            foreach (var dependency in service.DependsOn ?? [])
            {
                if (dependency?.Service is { } target && context.ServiceNames.Contains(target))
                {
                    edges[name].Add(target);
                }
            }
        }

        var explored = new HashSet<string>(StringComparer.Ordinal);
        var inCycle = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in edges.Keys)
        {
            CollectCycleMembers(name, edges, explored, [], inCycle);
        }

        for (var index = 0; index < manifest.Services.Count; index++)
        {
            if (manifest.Services[index]?.Name is { } name && inCycle.Contains(name))
            {
                errors.Add($"{prefix}.services[{index.ToString(CultureInfo.InvariantCulture)}] participates in a dependsOn cycle.");
            }
        }
    }

    /// <summary>
    ///     Depth-first walk that marks exactly the services sitting on a <c>dependsOn</c> cycle. A back edge to a node
    ///     already on the current path closes a cycle, so every node from that node to the end of the path is a member;
    ///     nodes that merely lead into a cycle are not.
    /// </summary>
    private static void CollectCycleMembers(
        string node,
        Dictionary<string, List<string>> edges,
        HashSet<string> explored,
        List<string> path,
        HashSet<string> inCycle)
    {
        var onPath = path.IndexOf(node);
        if (onPath >= 0)
        {
            for (var index = onPath; index < path.Count; index++)
            {
                _ = inCycle.Add(path[index]);
            }

            return;
        }

        if (!explored.Add(node))
        {
            return;
        }

        path.Add(node);
        foreach (var next in edges.GetValueOrDefault(node) ?? [])
        {
            CollectCycleMembers(next, edges, explored, path, inCycle);
        }

        path.RemoveAt(path.Count - 1);
    }

    private static void ValidateVariable(
        ApplicationVariable? variable,
        int index,
        string applicationPrefix,
        ApplicationValidationContext context,
        List<string> errors)
    {
        var prefix = $"{applicationPrefix}.variables[{index.ToString(CultureInfo.InvariantCulture)}]";

        if (variable is null)
        {
            errors.Add($"{prefix} is required.");
            return;
        }

        if (variable.Name is null || variable.Name.Length > 64 || !IdentifierRegex().IsMatch(variable.Name))
        {
            errors.Add($"{prefix}.name must match ^[A-Za-z_][A-Za-z0-9_]*$ and be at most 64 characters.");
        }
        else if (variable.Name.StartsWith(ReservedVariablePrefix, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.name must not start with the reserved prefix {ReservedVariablePrefix}.");
        }
        else if (!context.ReferencedVariableNames.Contains(variable.Name))
        {
            errors.Add($"{prefix}.name '{variable.Name}' is declared but never referenced by any service environment value.");
        }

        if (string.IsNullOrWhiteSpace(variable.Label) || variable.Label.Length > 80)
        {
            errors.Add($"{prefix}.label is required and must be at most 80 characters.");
        }

        if (variable.Type is null || !VariableTypeSet.Contains(variable.Type))
        {
            errors.Add($"{prefix}.type must be one of {string.Join(", ", VariableTypes)}.");
            return;
        }

        ValidateVariableShape(variable, prefix, errors);
        ValidateVariableValidation(variable, prefix, errors);
        ValidateVariableDefault(variable, prefix, errors);
    }

    private static void ValidateVariableShape(ApplicationVariable variable, string prefix, List<string> errors)
    {
        if (string.Equals(variable.Type, "secret", StringComparison.Ordinal) && variable.Default is not null)
        {
            errors.Add($"{prefix}.default must be null for a secret variable.");
        }

        var isEnum = string.Equals(variable.Type, "enum", StringComparison.Ordinal);
        if (isEnum)
        {
            if (variable.AllowedValues is null
                || variable.AllowedValues.Count == 0
                || variable.AllowedValues.Any(string.IsNullOrEmpty)
                || variable.AllowedValues.Distinct(StringComparer.Ordinal).Count() != variable.AllowedValues.Count)
            {
                errors.Add($"{prefix}.allowedValues is required and must be distinct for an enum variable.");
            }
        }
        else if (variable.AllowedValues is not null)
        {
            errors.Add($"{prefix}.allowedValues is only valid for an enum variable.");
        }

        if (string.Equals(variable.Type, "integer", StringComparison.Ordinal) && variable.Validation?.Pattern is not null)
        {
            errors.Add($"{prefix}.validation.pattern is not valid for an integer variable.");
        }
    }

    private static void ValidateVariableValidation(ApplicationVariable variable, string prefix, List<string> errors)
    {
        if (variable.Validation is not { } validation)
        {
            return;
        }

        if (validation.MinLength is < 0)
        {
            errors.Add($"{prefix}.validation.minLength must be at least 0.");
        }

        if (validation.MaxLength is { } maximum && maximum < (validation.MinLength ?? 0))
        {
            errors.Add($"{prefix}.validation.maxLength must be at least minLength.");
        }

        if (validation.Pattern is { } pattern && (pattern.Length > 200 || !CanCompile(pattern)))
        {
            errors.Add($"{prefix}.validation.pattern must be a non-backtracking regular expression of at most 200 characters.");
        }
    }

    private static void ValidateVariableDefault(ApplicationVariable variable, string prefix, List<string> errors)
    {
        if (variable.Default is not { } value)
        {
            return;
        }

        // The type predicate and the declared validation both bind, in every branch: an integer, a boolean or an
        // allowed enum member is still rejected when it violates the minLength/maxLength/pattern the same variable
        // declares, which is the constraint the install form will enforce against the operator's own value.
        var matchesType = variable.Type switch
        {
            "integer" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "boolean" => value is "true" or "false",
            "enum" => variable.AllowedValues?.Contains(value, StringComparer.Ordinal) is true,
            _ => true
        };

        var satisfied = matchesType && SatisfiesValidation(value, variable.Validation);

        if (!satisfied)
        {
            errors.Add($"{prefix}.default does not satisfy the declared type/validation.");
        }
    }

    private static bool SatisfiesValidation(string value, ApplicationVariableValidation? validation)
    {
        if (validation is null)
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

        if (validation.Pattern is not { } pattern || pattern.Length > 200 || !CanCompile(pattern))
        {
            return true;
        }

        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternCompileTimeout)
                .IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Whether <paramref name="pattern" /> compiles as a non-backtracking regular expression. A catalog pattern is
    ///     evaluated against user input at install time, so the engine removes catastrophic backtracking as a class:
    ///     a malformed pattern raises <see cref="ArgumentException" /> and one using a construct the non-backtracking
    ///     engine cannot express (a backreference, a lookaround) raises <see cref="NotSupportedException" />. Both fail
    ///     the catalog at load rather than at install.
    /// </summary>
    private static bool CanCompile(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternCompileTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    ///     A local route the "Open" action appends to <c>http://127.0.0.1:{port}</c>. A leading <c>//</c> would make
    ///     the browser read it as protocol-relative and navigate off the loopback origin entirely; a backslash is a
    ///     Windows separator no container route uses and a second way to spell an escape; a control character (or
    ///     <c>DEL</c>) has no meaning in a URL and only exists to smuggle something past a log or a UI.
    /// </summary>
    private static bool IsAcceptableOpenPath(string openPath)
    {
        if (openPath.Length is 0 or > MaxOpenPathLength
            || openPath[0] != '/'
            || openPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        return !openPath.Any(character => character is '\\' or < '\u0021' or '\u007F');
    }

    /// <summary>
    ///     Whether <paramref name="source" /> names exactly one file below the service's authored directory. The
    ///     installer joins it onto a host directory, so it must be relative, canonical and free of any segment that
    ///     resolves elsewhere or to the same file under a second spelling.
    /// </summary>
    private static bool IsAcceptableFileSource(string? source)
    {
        return !string.IsNullOrWhiteSpace(source)
               && !source.StartsWith('/')
               && !source.EndsWith('/')
               && !source.Contains('\\', StringComparison.Ordinal)
               && source.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsMountablePath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && path.StartsWith('/')
               && path.Length > 1
               && !path.EndsWith('/')
               && !path.Contains('\\', StringComparison.Ordinal)
               && HasOnlyOrdinarySegments(path);
    }

    /// <summary>
    ///     Whether every segment of <paramref name="path" /> names something. An empty segment (<c>//</c>, a trailing
    ///     <c>/</c>) or a <c>.</c> segment spells a path the container resolves to one the manifest already declared,
    ///     so the duplicate and nesting rules would compare two spellings of the same mount and pass both.
    ///     <c>..</c> is rejected here too — it is the same aliasing defect with an escape attached.
    /// </summary>
    private static bool HasOnlyOrdinarySegments(string path)
    {
        // The first element is the empty string in front of the leading '/', which is the one empty segment a
        // rooted path is allowed.
        return path.Split('/').Skip(count: 1).All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsSameOrNested(string candidate, string other)
    {
        return string.Equals(candidate, other, StringComparison.Ordinal)
               || candidate.StartsWith(other + "/", StringComparison.Ordinal)
               || other.StartsWith(candidate + "/", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The message for a path that <see cref="IsSameOrNested" /> rejected against an already-accepted one. The
    ///     three wordings exist because the relation is not symmetric to an author: the earlier entry can be the same
    ///     path, the parent directory, or the child that the new entry would have to become a directory to hold.
    /// </summary>
    private static string OverlapError(string path, string property, string value, string existing)
    {
        if (string.Equals(value, existing, StringComparison.Ordinal))
        {
            return $"{path}.{property} '{value}' is a duplicate.";
        }

        return value.StartsWith(existing + "/", StringComparison.Ordinal)
            ? $"{path}.{property} must not nest inside '{existing}'."
            : $"{path}.{property} must not contain '{existing}'.";
    }

    private static bool IsIso8601Utc(string value)
    {
        return value.EndsWith('Z')
               && DateTimeOffset.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal,
                   out var parsed)
               && parsed.Offset == TimeSpan.Zero;
    }

    private static IReadOnlySet<string> ToOrdinalSet(IReadOnlyList<string> values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    /// <summary>
    ///     The application-wide facts the per-service and per-variable rules read: which service names exist, which
    ///     publish a UI port, which declare a healthcheck, which variables are declared, and the running tallies the
    ///     cross-application rules (A17–A19) and the per-application port-uniqueness rule need.
    /// </summary>
    private sealed class ApplicationValidationContext
    {
        private ApplicationValidationContext(
            IReadOnlySet<string> serviceNames,
            IReadOnlySet<string> servicesWithUiPort,
            IReadOnlySet<string> servicesWithHealthcheck,
            IReadOnlySet<string> declaredVariableNames,
            int openPathCount,
            bool declaresExtraHosts,
            bool declaresPort)
        {
            ServiceNames = serviceNames;
            ServicesWithUiPort = servicesWithUiPort;
            ServicesWithHealthcheck = servicesWithHealthcheck;
            DeclaredVariableNames = declaredVariableNames;
            OpenPathCount = openPathCount;
            DeclaresExtraHosts = declaresExtraHosts;
            DeclaresPort = declaresPort;
        }

        public IReadOnlySet<string> ServiceNames { get; }

        public IReadOnlySet<string> ServicesWithUiPort { get; }

        public IReadOnlySet<string> ServicesWithHealthcheck { get; }

        public IReadOnlySet<string> DeclaredVariableNames { get; }

        public HashSet<string> ReferencedVariableNames { get; } = new(StringComparer.Ordinal);

        public HashSet<int> SeenPreferredHostPorts { get; } = [];

        public int OpenPathCount { get; }

        public bool DeclaresExtraHosts { get; }

        public bool DeclaresPort { get; }

        public static ApplicationValidationContext Create(ApplicationManifest manifest)
        {
            var serviceNames = new HashSet<string>(StringComparer.Ordinal);
            var servicesWithUiPort = new HashSet<string>(StringComparer.Ordinal);
            var servicesWithHealthcheck = new HashSet<string>(StringComparer.Ordinal);
            var openPathCount = 0;
            var declaresExtraHosts = false;
            var declaresPort = false;

            foreach (var service in manifest.Services)
            {
                if (service?.Name is not { } name)
                {
                    continue;
                }

                _ = serviceNames.Add(name);

                if (service.Healthcheck is not null)
                {
                    _ = servicesWithHealthcheck.Add(name);
                }

                if (service.ExtraHosts is { Count: > 0 })
                {
                    declaresExtraHosts = true;
                }

                foreach (var port in service.Ports ?? [])
                {
                    if (port is null)
                    {
                        continue;
                    }

                    declaresPort = true;

                    if (string.Equals(port.Role, "ui", StringComparison.Ordinal))
                    {
                        _ = servicesWithUiPort.Add(name);
                    }

                    if (port.OpenPath is not null)
                    {
                        openPathCount++;
                    }
                }
            }

            var variableNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variable in manifest.Variables)
            {
                if (variable?.Name is { } variableName)
                {
                    _ = variableNames.Add(variableName);
                }
            }

            return new ApplicationValidationContext(
                serviceNames,
                servicesWithUiPort,
                servicesWithHealthcheck,
                variableNames,
                openPathCount,
                declaresExtraHosts,
                declaresPort);
        }
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{1,40}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApplicationIdRegex();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,30}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ServiceNameRegex();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"\A[^\s@]+@sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigestPinnedImageRegex();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sha256HexRegex();

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_-]*)\}",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SubstitutionTokenRegex();
}
