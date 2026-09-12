namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Turns a stored manifest, the instance's decrypted variables and the host ports already held into the exact
///     set of containers to create, in the order to create them.
///     <para>
///         It is the only place decrypted values exist outside the store call that read them. Nothing on this path
///         logs a specification, a variable map or an environment dictionary, and both plan records suppress their
///         printers for the same reason.
///     </para>
/// </summary>
internal static partial class DeploymentPlanner
{
    /// <summary>The prefix of the per-service built-in carrying a service's own published host port.</summary>
    internal const string UiHostPortPrefix = "XE_UI_HOST_PORT_";

    private const string UserIdVariable = "XE_UID";

    private const string GroupIdVariable = "XE_GID";

    private const string InstanceIdVariable = "XE_INSTANCE_ID";

    /// <summary>The container-facing <c>host:port</c> of this node's bridge. Present only when the bridge is open.</summary>
    private const string BridgeEndpointVariable = "XE_BRIDGE_ENDPOINT";

    /// <summary>The instance's own bridge credential. Present only when the bridge is open.</summary>
    private const string BridgeTokenVariable = "XE_BRIDGE_TOKEN";

    private const string HealthyCondition = "healthy";

    /// <summary>
    ///     Plans one deployment attempt. <paramref name="variables" /> is decrypted and <paramref name="uiHostPorts" />
    ///     is what the port allocator is still holding, so every value a container will see is known before the first
    ///     create — which is the whole reason the host port is chosen by the engine rather than by the daemon.
    /// </summary>
    /// <exception cref="ExternalAppManifestException">A dependency cycle, an unknown dependency or a mount collision.</exception>
    /// <exception cref="ExternalAppConfigurationException">An unresolved or malformed <c>${…}</c> token.</exception>
    internal static DeploymentPlan Plan(ApplicationManifest manifest,
        Guid instanceId,
        string installId,
        IReadOnlyDictionary<string, string> variables,
        ResolvedContainerIdentity identity,
        IReadOnlyList<ExternalAppHostPort> uiHostPorts,
        ExternalAppStoragePaths storage,
        ContainerBridgeGrant? bridgeGrant = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(uiHostPorts);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(installId);

        var ordered = TopologicalOrder(manifest);
        var declared = manifest.Variables.ToDictionary(static variable => variable.Name, StringComparer.Ordinal);
        var builtIns = BuildBuiltIns(instanceId, identity, uiHostPorts, bridgeGrant);
        var hostSources = new Dictionary<string, string>(StringComparer.Ordinal);

        var services = new List<ServiceDeployment>(ordered.Count);
        foreach (var service in ordered)
        {
            var environment = ResolveEnvironment(service, declared, variables, builtIns);
            var mounts = BuildMounts(service, storage, hostSources);
            var publications = uiHostPorts
                              .Where(port => string.Equals(port.Service, service.Name, StringComparison.Ordinal))
                              .Select(static port => ApplicationContainerPolicy.Publication(port.ContainerPort, port.HostPort))
                              .ToArray();

            var specification = ApplicationContainerPolicy.BuildSpecification(service, manifest, instanceId, installId, environment, mounts, publications);

            services.Add(new ServiceDeployment(service.Name,
                specification.Name,
                specification,
                [.. service.DependsOn.Select(static dependency => new ServiceDependency(dependency.Service, string.Equals(dependency.Condition, HealthyCondition, StringComparison.Ordinal)))],
                [.. publications.Select(static publication => publication.ContainerPort)]));
        }

        return new DeploymentPlan(ExternalAppLabels.NetworkName(instanceId), services);
    }

    private static Dictionary<string, string> BuildBuiltIns(Guid instanceId,
        ResolvedContainerIdentity identity,
        IReadOnlyList<ExternalAppHostPort> uiHostPorts,
        ContainerBridgeGrant? bridgeGrant)
    {
        var builtIns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UserIdVariable] = identity.UserId.ToString(CultureInfo.InvariantCulture),
            [GroupIdVariable] = identity.GroupId.ToString(CultureInfo.InvariantCulture),
            [InstanceIdVariable] = instanceId.ToString("N", CultureInfo.InvariantCulture)
        };

        // Both bridge built-ins or neither, and only when this node actually opened a bridge. A manifest that
        // references either token on a node without one fails plan-time validation as an undeclared token, which is
        // the honest answer: injecting an endpoint the container cannot reach would fail later and less clearly.
        if (bridgeGrant is not null)
        {
            builtIns[BridgeEndpointVariable] = bridgeGrant.Endpoint;
            builtIns[BridgeTokenVariable] = bridgeGrant.Token;
        }

        // One entry per service that publishes something, carrying its FIRST published port: the token is
        // XE_UI_HOST_PORT_<service>, so a service with two published ports still has one browser-visible address.
        foreach (var port in uiHostPorts)
        {
            _ = builtIns.TryAdd(UiHostPortPrefix + port.Service, port.HostPort.ToString(CultureInfo.InvariantCulture));
        }

        return builtIns;
    }

    /// <summary>
    ///     The deployment order by name alone, for a caller that needs the order and nothing else a plan carries —
    ///     a stop, which reverses it. Exposed so that caller does not have to build a whole plan, and with it a
    ///     container identity it has no daemon to resolve against, to read one list back out.
    /// </summary>
    /// <exception cref="ExternalAppManifestException">The manifest names a duplicate, a missing or a cyclic dependency.</exception>
    internal static IReadOnlyList<string> ServiceOrder(ApplicationManifest manifest)
    {
        return [.. TopologicalOrder(manifest).Select(static service => service.Name)];
    }

    /// <summary>
    ///     Orders the services so every dependency is created before its dependants. The catalog validator rejects a
    ///     cycle too; this is the authority for what is actually deployed, because the snapshot being deployed may
    ///     have been admitted by an older validator.
    /// </summary>
    private static List<ApplicationService> TopologicalOrder(ApplicationManifest manifest)
    {
        if (manifest.Services.GroupBy(static service => service.Name, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1) is { } duplicate)
        {
            throw new ExternalAppManifestException($"The manifest declares service '{duplicate.Key}' more than once.");
        }

        var byName = manifest.Services.ToDictionary(static service => service.Name, StringComparer.Ordinal);

        var ordered = new List<ApplicationService>(manifest.Services.Count);
        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);

        foreach (var service in manifest.Services)
        {
            Visit(service, byName, state, ordered);
        }

        return ordered;
    }

    private static void Visit(ApplicationService service,
        Dictionary<string, ApplicationService> byName,
        Dictionary<string, VisitState> state,
        List<ApplicationService> ordered)
    {
        if (state.TryGetValue(service.Name, out var visited))
        {
            if (visited == VisitState.InProgress)
            {
                throw new ExternalAppManifestException($"The manifest's dependsOn graph contains a cycle through service '{service.Name}'.");
            }

            return;
        }

        if (service.DependsOn.FirstOrDefault(dependency => !byName.ContainsKey(dependency.Service)) is { } missing)
        {
            throw new ExternalAppManifestException($"Service '{service.Name}' depends on '{missing.Service}', which the manifest does not declare.");
        }

        state[service.Name] = VisitState.InProgress;
        foreach (var target in service.DependsOn.Select(dependency => byName[dependency.Service]))
        {
            Visit(target, byName, state, ordered);
        }

        state[service.Name] = VisitState.Done;
        ordered.Add(service);
    }

    private static IReadOnlyDictionary<string, string> ResolveEnvironment(ApplicationService service,
        Dictionary<string, ApplicationVariable> declared,
        IReadOnlyDictionary<string, string> variables,
        Dictionary<string, string> builtIns)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in service.Environment)
        {
            resolved[entry.Key] = Substitute(entry.Value ?? string.Empty, service.Name, declared, variables, builtIns);
        }

        return resolved;
    }

    /// <summary>
    ///     Resolves every <c>${NAME}</c> in one value. The three rules are asymmetric on purpose: a bare <c>$</c> is a
    ///     literal, a <c>${</c> that does not close on a valid token is an error rather than a literal, and a
    ///     well-formed token resolves declared value, then declared default, then built-in, then fails by name.
    /// </summary>
    private static string Substitute(string value,
        string serviceName,
        Dictionary<string, ApplicationVariable> declared,
        IReadOnlyDictionary<string, string> variables,
        Dictionary<string, string> builtIns)
    {
        var builder = new StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length)
        {
            var dollar = value.IndexOf('$', index);
            if (dollar < 0)
            {
                _ = builder.Append(value, index, value.Length - index);
                break;
            }

            _ = builder.Append(value, index, dollar - index);

            if (dollar + 1 >= value.Length || value[dollar + 1] != '{')
            {
                // A bare '$' is a literal. Shell-style $NAME is not substitution here, and treating it as one would
                // silently rewrite a password that happens to start with a dollar sign.
                _ = builder.Append('$');
                index = dollar + 1;
                continue;
            }

            var match = SubstitutionTokenRegex().Match(value, dollar);
            if (!match.Success || match.Index != dollar)
            {
                // A Compose-style default such as ${FOO:-bar} lands here. Passing it through verbatim would put that
                // text into the container as if it were the value.
                throw new ExternalAppConfigurationException(
                    $"Service '{serviceName}' declares an environment value containing a malformed '${{…}}' token; only '${{NAME}}' is supported.");
            }

            _ = builder.Append(Resolve(match.Groups["name"].Value, serviceName, declared, variables, builtIns));
            index = dollar + match.Length;
        }

        return builder.ToString();
    }

    private static string Resolve(string name,
        string serviceName,
        Dictionary<string, ApplicationVariable> declared,
        IReadOnlyDictionary<string, string> variables,
        Dictionary<string, string> builtIns)
    {
        if (declared.TryGetValue(name, out var definition))
        {
            if (variables.TryGetValue(name, out var supplied) && supplied is not null)
            {
                return supplied;
            }

            if (definition.Default is { } fallback)
            {
                return fallback;
            }

            // An optional variable with neither a value nor a default substitutes to the empty string, and the
            // environment KEY is still emitted: an image that branches on "is the variable present" must see the
            // same shape whether or not the user filled the field in.
            if (!definition.Required)
            {
                return string.Empty;
            }

            throw new ExternalAppConfigurationException($"Service '{serviceName}' needs the required variable '{name}', which has no value and no default.");
        }

        if (builtIns.TryGetValue(name, out var builtIn))
        {
            return builtIn;
        }

        // The bridge names first, because this is the failure an operator will actually meet: a manifest that needs
        // the bridge, installed on a node that opened none (the feature switched off, no IPv4 address, no qualifying
        // interface, or a bind address this host does not own). Still a validation error and still refused — an
        // endpoint the container cannot reach would fail later and less clearly — but named for the cause rather
        // than reported as a token the user has never heard of.
        if (string.Equals(name, BridgeEndpointVariable, StringComparison.Ordinal)
            || string.Equals(name, BridgeTokenVariable, StringComparison.Ordinal))
        {
            throw new ExternalAppConfigurationException(
                $"Service '{serviceName}' needs the container bridge, which this node did not open, so '${{{name}}}' has no value. "
                + $"The bridge requires '{ContainerBridgeOptions.SectionName}:{nameof(ContainerBridgeOptions.Enabled)}' and an IPv4 host interface it can bind.");
        }

        // A XE_UI_HOST_PORT_<service> naming a service that publishes nothing lands here, and so does a token the
        // manifest never declared. Both are errors rather than empty strings: a URL built from an empty port is a
        // container that starts and then cannot be reached.
        throw new ExternalAppConfigurationException($"Service '{serviceName}' references '${{{name}}}', which is neither a declared variable nor a built-in.");
    }

    /// <summary>
    ///     Builds one service's mounts and proves no two of them, across the whole application, share a host
    ///     directory. Container targets are checked within the service: two containers mounting the same path is
    ///     ordinary, two containers backed by the same host directory is the bug the per-service layout exists to
    ///     prevent.
    /// </summary>
    private static IReadOnlyList<ContainerMount> BuildMounts(ApplicationService service,
        ExternalAppStoragePaths storage,
        Dictionary<string, string> hostSources)
    {
        var mounts = new List<ContainerMount>(service.Storage.Count + service.Files.Count);
        var targets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in service.Storage)
        {
            Add(storage.VolumePath(service.Name, ExternalAppStorageLayout.ValidateRelativeName(entry.Name, "storage[].name")),
                entry.ContainerPath,
                readOnly: false);
        }

        foreach (var file in service.Files)
        {
            Add(storage.FilePath(service.Name, ExternalAppStorageLayout.ValidateRelativeName(file.Source, "files[].source")),
                file.ContainerPath,
                readOnly: true);
        }

        return mounts;

        void Add(string hostPath, string? containerPath, bool readOnly)
        {
            var target = DockerSandboxPaths.NormalizePosix(containerPath ?? string.Empty);
            if (target.Length <= 1)
            {
                throw new ExternalAppManifestException($"Service '{service.Name}' declares a mount with no container path.");
            }

            if (!targets.Add(target))
            {
                throw new ExternalAppManifestException($"Service '{service.Name}' declares two mounts at the container path '{target}'.");
            }

            var source = Path.GetFullPath(hostPath);
            if (hostSources.TryGetValue(source, out var owner))
            {
                throw new ExternalAppManifestException($"Services '{owner}' and '{service.Name}' would both be backed by the host directory '{source}'.");
            }

            hostSources[source] = service.Name;
            mounts.Add(new ContainerMount { HostPath = source, ContainerPath = target, ReadOnly = readOnly });
        }
    }

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_-]*)\}",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SubstitutionTokenRegex();

    private enum VisitState
    {
        InProgress = 0,
        Done = 1
    }
}

/// <summary>Everything one deployment attempt will create, in the order to create it.</summary>
internal sealed record DeploymentPlan(string NetworkName, IReadOnlyList<ServiceDeployment> Services)
{
    // Suppressed for the same reason S0 suppresses the specification's printer: the services carry environments
    // holding values decrypted from the instance's variables, and one LogDebug("{Plan}", plan) would write an
    // application's admin password to the node log. See ContainerSpecification.PrintMembers for why the four
    // analyzers are silenced rather than obeyed — every fix they suggest changes the signature into one the
    // compiler no longer recognises as the record's printer.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>One service of a plan: what to create, what it must wait for, and what it publishes.</summary>
internal sealed record ServiceDeployment(string ServiceName,
    string ContainerName,
    ContainerSpecification Specification,
    IReadOnlyList<ServiceDependency> DependsOn,
    IReadOnlyList<int> UiContainerPorts)
{
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>
///     A start-ordering edge. <see cref="RequiresHealthy" /> rather than the manifest's condition string, so the
///     waiter branches on a boolean it cannot mistype.
/// </summary>
internal sealed record ServiceDependency(string Service, bool RequiresHealthy);
