namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Authorizes a Development-marked request immediately after the runtime selects a cloud client and before the
///     request reaches that client's transport. Implementations must use only the content-free metadata supplied here.
/// </summary>
public interface ICloudEgressAuthorizer
{
    void Authorize(CloudEgressAuthorizationRequest request);
}
