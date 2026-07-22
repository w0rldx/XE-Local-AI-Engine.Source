namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Raised when a Development-marked request cannot be authorized before cloud transport.
/// </summary>
public sealed class CloudEgressAuthorizationException(string reason) : InvalidOperationException(reason);
