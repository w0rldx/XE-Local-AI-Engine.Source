namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     The catalog-card join, in one place because both catalog routes perform it and a card that offered "Install" on
///     an application that already has an instance would be a different answer from the same data.
/// </summary>
internal static class ExternalAppCatalogEndpointSupport
{
    /// <summary>
    ///     Keys the installed instances by application id. V1 allows one instance per application, so a later row for
    ///     the same application would be a state the install gate refuses; the first wins rather than throwing, because
    ///     a catalog read is not the place to discover it.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ExternalAppInstanceSummary>> InstalledByApplicationIdAsync(IExternalAppService apps,
        CancellationToken cancellationToken)
    {
        var instances = await apps.ListAsync(cancellationToken).ConfigureAwait(false);

        var installed = new Dictionary<string, ExternalAppInstanceSummary>(instances.Count, StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            _ = installed.TryAdd(instance.ApplicationId, instance);
        }

        return installed;
    }
}
