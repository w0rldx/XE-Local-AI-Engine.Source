namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>Whether a model routes to a cloud provider, and whether that answer had to be assumed.</summary>
internal sealed record CloudRoutingClassification(bool RoutesToCloud, bool Faulted);

/// <summary>
///     The single cloud-routing classification both the per-turn chat path (<see cref="ChatTurnResolver" />) and the
///     per-participant path (<see cref="ModelCapabilityResolver" />) use. Shared rather than mirrored, because the two
///     were byte-identical copies and a change to only one of them would let a model be classified local on one path
///     while it egresses to the cloud on the other.
/// </summary>
internal static class CloudRoutingClassifier
{
    /// <summary>
    ///     Classifies whether <paramref name="model" /> would ROUTE to a cloud provider, reading the cloud factory's
    ///     shared short-TTL routing snapshot — the same source the send path routes from — so classification and
    ///     routing cannot diverge. On any snapshot read failure the result FAILS CLOSED
    ///     (<c>RoutesToCloud: true, Faulted: true</c>) so the private-data gates withhold rather than leak.
    /// </summary>
    public static CloudRoutingClassification Classify(IActiveCloudChatClientFactory cloudChatClientFactory, ILogger logger, string model)
    {
        try
        {
            return new CloudRoutingClassification(cloudChatClientFactory.IsCloudProviderSelected(model), Faulted: false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cloud routing for '{Model}' could not be resolved; failing closed to cloud for the private-data gate.", model);
            return new CloudRoutingClassification(RoutesToCloud: true, Faulted: true);
        }
    }
}
