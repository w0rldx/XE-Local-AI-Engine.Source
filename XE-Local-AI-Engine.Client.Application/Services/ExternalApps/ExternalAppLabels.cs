namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;

/// <summary>
///     The four labels every container and network this feature creates carries, and the only way anything is found
///     again.
/// </summary>
/// <remarks>
///     Ownership is never inferred from the name <c>xe-app-&lt;instanceId:N&gt;</c>: a second installation pointed at
///     the same daemon would generate the same names, and a teardown matching on one would remove another
///     installation's containers. The owner label alone is not enough either — its value is a constant, so every
///     installation carries it — which is why the install id is a label of its own.
/// </remarks>
internal static class ExternalAppLabels
{
    /// <summary>Marks a container or network as belonging to this feature rather than to Development Mode.</summary>
    internal const string Owner = "com.xe-local-ai-engine.owner";

    /// <summary>The value <see cref="Owner" /> carries.</summary>
    internal const string OwnerValue = "external-apps";

    /// <summary>The per-installation id, the same source Development Mode's install label uses.</summary>
    internal const string Install = "com.xe-local-ai-engine.install";

    /// <summary>The instance the container belongs to, as <c>N</c>-formatted GUID.</summary>
    internal const string Instance = "com.xe-local-ai-engine.external-app.instance";

    /// <summary>The manifest service the container runs.</summary>
    internal const string Service = "com.xe-local-ai-engine.external-app.service";

    /// <summary>
    ///     Marks the short-lived engine-owned container that deletes an instance's volume contents.
    /// </summary>
    /// <remarks>
    ///     It carries the instance's own three labels, so a teardown, the boot pass's orphan sweep and an operator's
    ///     <c>docker ps</c> filter all account for it, and it carries no <see cref="Service" /> label, so nothing that
    ///     keys containers by service — reuse, the state observer, the reconciler — can mistake it for one of the
    ///     application's own.
    /// </remarks>
    internal const string Helper = "com.xe-local-ai-engine.external-app.helper";

    /// <summary>The value <see cref="Helper" /> carries. One helper role exists.</summary>
    internal const string StorageWipeValue = "storage-wipe";

    /// <summary>The engine-generated network name for one instance.</summary>
    internal static string NetworkName(Guid instanceId)
    {
        return "xe-app-" + instanceId.ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>The engine-generated container name for one service of one instance.</summary>
    internal static string ContainerName(Guid instanceId, string serviceName)
    {
        return NetworkName(instanceId) + "-" + serviceName;
    }

    /// <summary>The engine-generated container name for one instance's storage-wipe helper.</summary>
    internal static string StorageHelperContainerName(Guid instanceId)
    {
        return NetworkName(instanceId) + "-storage-helper";
    }

    /// <summary>The instance's own label set plus <see cref="Helper" />; see that constant for why both halves matter.</summary>
    internal static Dictionary<string, string> ForStorageHelper(string installId, Guid instanceId)
    {
        var labels = For(installId, instanceId);
        labels[Helper] = StorageWipeValue;
        return labels;
    }

    /// <summary>
    ///     The label set for one container, or — with <paramref name="serviceName" /> null — for the instance
    ///     network and for the daemon-side filter every teardown, listing and reconcile pass uses.
    /// </summary>
    internal static Dictionary<string, string> For(string installId, Guid instanceId, string? serviceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installId);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Owner] = OwnerValue,
            [Install] = installId,
            [Instance] = instanceId.ToString("N", CultureInfo.InvariantCulture)
        };

        if (serviceName is not null)
        {
            labels[Service] = serviceName;
        }

        return labels;
    }
}
