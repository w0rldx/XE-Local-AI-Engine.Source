namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Describes whether a Development-marked request carried a complete, strongly typed authorization envelope.
/// </summary>
public enum CloudEgressAuthorizationCarrierState
{
    Valid = 0,
    MissingEnvelope = 1,
    MalformedPurpose = 2,
    MalformedEnvelope = 3
}
