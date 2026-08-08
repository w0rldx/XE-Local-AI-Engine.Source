namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Extensions.AI;

/// <summary>
///     Owns the namespaced <see cref="ChatOptions.AdditionalProperties" /> keys used by Development cloud requests.
/// </summary>
public static class DevelopmentCloudAuthorizationMetadata
{
    public const string PurposeKey = "xe.development.purpose";
    public const string EnvelopeKey = "xe.development.cloud-authorization";
    public const string PurposeValue = "development";

    /// <summary>
    ///     Marks <paramref name="options" /> as a Development request and attaches the immutable authorization value.
    /// </summary>
    public static void Apply(ChatOptions options, DevelopmentCloudAuthorizationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(envelope);

        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[PurposeKey] = PurposeValue;
        options.AdditionalProperties[EnvelopeKey] = envelope;
    }

    internal static bool IsDevelopmentMarked(ChatOptions? options)
    {
        var properties = options?.AdditionalProperties;
        return properties is not null && (properties.ContainsKey(PurposeKey) || properties.ContainsKey(EnvelopeKey));
    }

    internal static bool TryCreateRequest(ChatOptions? options,
        string providerName,
        out CloudEgressAuthorizationRequest? request)
    {
        var properties = options?.AdditionalProperties;
        if (properties is null)
        {
            request = null;
            return false;
        }

        var hasPurpose = properties.TryGetValue(PurposeKey, out var purpose);
        var hasEnvelope = properties.TryGetValue(EnvelopeKey, out var envelopeValue);
        if (!hasPurpose && !hasEnvelope)
        {
            request = null;
            return false;
        }

        CloudEgressAuthorizationCarrierState state;
        if (!hasPurpose || purpose is not string purposeText || !string.Equals(purposeText, PurposeValue, StringComparison.Ordinal))
        {
            state = CloudEgressAuthorizationCarrierState.MalformedPurpose;
        }
        else if (!hasEnvelope)
        {
            state = CloudEgressAuthorizationCarrierState.MissingEnvelope;
        }
        else if (envelopeValue is not DevelopmentCloudAuthorizationEnvelope)
        {
            state = CloudEgressAuthorizationCarrierState.MalformedEnvelope;
        }
        else
        {
            state = CloudEgressAuthorizationCarrierState.Valid;
        }

        request = new CloudEgressAuthorizationRequest(providerName,
            options?.ModelId,
            state,
            envelopeValue as DevelopmentCloudAuthorizationEnvelope);
        return true;
    }
}
