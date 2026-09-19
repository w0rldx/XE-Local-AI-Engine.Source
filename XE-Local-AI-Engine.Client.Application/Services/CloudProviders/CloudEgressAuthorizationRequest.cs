namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Content-free metadata presented to the final selected-cloud authorization boundary.
/// </summary>
public sealed class CloudEgressAuthorizationRequest
{
    public required string ProviderName { get; init; }

    public required string? ModelId { get; init; }

    public required CloudEgressAuthorizationCarrierState CarrierState { get; init; }

    public required DevelopmentCloudAuthorizationEnvelope? Envelope { get; init; }
}
