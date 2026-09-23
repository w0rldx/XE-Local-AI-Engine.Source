namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Persists the update channel this node follows, then returns the status it produces under the new policy.
/// </summary>
/// <remarks>
///     Changing the channel NEVER downloads, applies or restarts anything — applying stays an explicit, separate
///     operator action. The immediate check deliberately skips the 10-minute rate floor the startup check and the
///     manual refresh keep: a check under a NEW policy is not a duplicate of the previous one. The endpoint is
///     desktop-only and Operator-gated, so this is not a rate-limit hole an unauthenticated caller can use.
/// </remarks>
public sealed class SetAppUpdateChannelEndpoint : Endpoint<SetAppUpdateChannelRequest, AppUpdateStatusResponse>, IDesktopOnlyEndpoint
{
    private readonly IAppUpdateService _updateService;

    public SetAppUpdateChannelEndpoint(IAppUpdateService updateService)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        _updateService = updateService;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.AppUpdate.Channel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetAppUpdateChannelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!AppUpdateChannelNames.TryParse(req.Channel, out var channel))
        {
            // Unreachable: the validator rejects anything outside the three literals before this runs. Defence in
            // depth, because a silently defaulted channel would move the node to Stable without saying so.
            AddError("Update channel must be stable, preview or development.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var snapshot = await _updateService.SetChannelAsync(channel, ct);
        await Send.OkAsync(GetAppUpdateStatusEndpoint.ToResponse(snapshot), ct);
    }
}
