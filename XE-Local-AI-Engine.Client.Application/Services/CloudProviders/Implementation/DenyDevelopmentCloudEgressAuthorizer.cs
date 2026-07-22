namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

/// <summary>
///     Fail-closed Development boundary. Ordinary Chat never reaches this service; every Development-marked cloud request is
///     rejected until the production bundle validator is introduced by the later approved gate.
/// </summary>
public sealed class DenyDevelopmentCloudEgressAuthorizer : ICloudEgressAuthorizer
{
    public void Authorize(CloudEgressAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new CloudEgressAuthorizationException("Development cloud egress is disabled until a production authorizer is configured.");
    }
}
