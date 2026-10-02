namespace XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>
///     Downloads the scrubbed server-side support bundle (<c>application/zip</c>): node info, log tails and child-process
///     output tails. Built in memory; bounded by construction.
/// </summary>
public sealed class GetSupportBundleEndpoint : EndpointWithoutRequest
{
    private readonly ISupportBundleService _bundles;
    private readonly AppUpdateHostContext _hostContext;

    public GetSupportBundleEndpoint(ISupportBundleService bundles, AppUpdateHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        ArgumentNullException.ThrowIfNull(hostContext);
        _bundles = bundles;
        _hostContext = hostContext;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Diagnostics.SupportBundle);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<byte[]>(StatusCodes.Status200OK, "application/zip"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var bundle = await _bundles.BuildAsync(_hostContext.IsShellOwned, ct);
        await Send.BytesAsync(bundle.Zip.ToArray(), bundle.FileName, "application/zip", cancellation: ct);
    }
}
