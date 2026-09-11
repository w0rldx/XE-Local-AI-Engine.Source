namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>What one service of an application is granted, as opposed to what the application as a whole declares.</summary>
public sealed record ExternalAppServicePermissions(string ServiceName,
    IReadOnlySet<string> Capabilities,
    bool WritableRootFilesystem,
    IReadOnlySet<string> PublishedPorts,
    IReadOnlySet<string> ExtraHosts);

/// <summary>
///     Everything an application may do, computed from one manifest. The single authority behind the install
///     preview, the update preview, update admission, the acknowledgement payload and the permission panel — one
///     derivation, so the disclosure a user accepts and the check an update runs cannot disagree.
/// </summary>
/// <remarks>
///     The grants are kept PER SERVICE because a union hides real widenings: adding <c>CHOWN</c> to one service is
///     invisible when a sibling already has it, and making a second service writable is invisible when the first
///     already is. The application-level flags stay application-level because that is how the manifest declares
///     them.
/// </remarks>
public sealed record ExternalAppEffectivePermissions(bool Internet,
    bool LocalNetwork,
    string HostFiles,
    string Gpu,
    IReadOnlyDictionary<string, ExternalAppServicePermissions> Services)
{
    /// <summary>The eight names a widening is reported as, in the order they are emitted.</summary>
    public static IReadOnlyList<string> Vocabulary { get; } =
    [
        "internet", "localNetwork", "hostFiles", "gpu", "capabilities", "writableRootFilesystem", "publishedPorts", "extraHosts"
    ];

    /// <summary>Computes the effective permissions of one manifest.</summary>
    public static ExternalAppEffectivePermissions From(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var services = new Dictionary<string, ExternalAppServicePermissions>(StringComparer.Ordinal);
        foreach (var service in manifest.Services)
        {
            services[service.Name] = new ExternalAppServicePermissions(service.Name,
                new HashSet<string>(service.CapAdd ?? [], StringComparer.Ordinal),
                !service.ReadOnlyRootFilesystem,
                new HashSet<string>((service.Ports ?? []).Select(port => Port(service.Name, port)), StringComparer.Ordinal),
                new HashSet<string>(service.ExtraHosts ?? [], StringComparer.Ordinal));
        }

        return new ExternalAppEffectivePermissions(manifest.Permissions.Internet,
            manifest.Permissions.LocalNetwork,
            manifest.Permissions.HostFiles,
            manifest.Permissions.Gpu,
            services);
    }

    /// <summary>
    ///     The names of every grant <paramref name="target" /> adds over <paramref name="installed" />, empty when the
    ///     target grants nothing new. A service new in the target contributes everything it declares; a service that
    ///     has left contributes nothing, because losing a grant is not a widening.
    /// </summary>
    public static IReadOnlyList<string> Diff(ExternalAppEffectivePermissions installed, ExternalAppEffectivePermissions target)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(target);

        var added = new List<string>();

        if (!installed.Internet && target.Internet)
        {
            added.Add("internet");
        }

        if (!installed.LocalNetwork && target.LocalNetwork)
        {
            added.Add("localNetwork");
        }

        if (HostFilesRank(target.HostFiles) > HostFilesRank(installed.HostFiles))
        {
            added.Add("hostFiles");
        }

        if (GpuRank(target.Gpu) > GpuRank(installed.Gpu))
        {
            added.Add("gpu");
        }

        AddServiceWidenings(installed, target, added);

        return added;
    }

    private static void AddServiceWidenings(ExternalAppEffectivePermissions installed,
        ExternalAppEffectivePermissions target,
        List<string> added)
    {
        var capabilities = false;
        var writable = false;
        var ports = false;
        var extraHosts = false;

        foreach (var service in target.Services.Values)
        {
            // A service the installed manifest does not have contributes everything it declares: it is all new.
            var before = installed.Services.GetValueOrDefault(service.ServiceName);

            capabilities |= service.Capabilities.Any(capability => before is null || !before.Capabilities.Contains(capability));
            writable |= service.WritableRootFilesystem && (before is null || !before.WritableRootFilesystem);
            ports |= service.PublishedPorts.Any(port => before is null || !before.PublishedPorts.Contains(port));
            extraHosts |= service.ExtraHosts.Any(host => before is null || !before.ExtraHosts.Contains(host));
        }

        if (capabilities)
        {
            added.Add("capabilities");
        }

        if (writable)
        {
            added.Add("writableRootFilesystem");
        }

        if (ports)
        {
            added.Add("publishedPorts");
        }

        if (extraHosts)
        {
            added.Add("extraHosts");
        }
    }

    private static string Port(string serviceName, ApplicationPort port)
    {
        return serviceName + ":" + port.ContainerPort.ToString(CultureInfo.InvariantCulture);
    }

    private static int HostFilesRank(string? value)
    {
        return value switch
        {
            "readWrite" => 2,
            "readOnly" => 1,
            _ => 0
        };
    }

    private static int GpuRank(string? value)
    {
        return value switch
        {
            "required" => 2,
            "optional" => 1,
            _ => 0
        };
    }
}
