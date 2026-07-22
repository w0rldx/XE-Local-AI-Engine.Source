namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Content-free metadata presented to the final selected-cloud authorization boundary.
/// </summary>
public sealed record CloudEgressAuthorizationRequest(
    string ProviderName,
    string? ModelId,
    CloudEgressAuthorizationCarrierState CarrierState,
    DevelopmentCloudAuthorizationEnvelope? Envelope);
