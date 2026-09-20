namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Read-only app self-update status: the running version, the available version when newer, whether an update is
///     available, whether this build is configured and desktop, plus a sanitized check status.
/// </summary>
/// <remarks>
///     Reads the shared <see cref="IAppUpdateState" /> snapshot, computed at startup; <c>?refresh=true</c> forces a
///     fresh GitHub check, subject to a 10-minute rate-limit floor.
/// </remarks>
public sealed class GetAppUpdateStatusEndpoint : Endpoint<GetAppUpdateStatusRequest, AppUpdateStatusResponse>, IDesktopOnlyEndpoint
{
    // Minimum spacing between anonymous live GitHub refreshes.
    internal static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly IAppUpdateState _updateState;
    private readonly IAppUpdateService _updateService;

    public GetAppUpdateStatusEndpoint(IAppUpdateState updateState, IAppUpdateService updateService)
    {
        ArgumentNullException.ThrowIfNull(updateState);
        ArgumentNullException.ThrowIfNull(updateService);
        _updateState = updateState;
        _updateService = updateService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.AppUpdate.Status);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetAppUpdateStatusRequest req, CancellationToken ct)
    {
        // The service owns both the stale check and serialization so concurrent endpoint requests cannot start duplicate
        // anonymous GitHub calls after observing the same cached snapshot.
        var snapshot = req.Refresh ?? false
            ? await _updateService.RefreshIfStaleAsync(MinRefreshInterval, ct)
            : _updateState.Current;

        await Send.OkAsync(ToResponse(snapshot), ct);
    }

    private static AppUpdateStatusResponse ToResponse(AppUpdateSnapshot snapshot)
    {
        return new AppUpdateStatusResponse
        {
            CurrentVersion = snapshot.CurrentVersion,
            AvailableVersion = snapshot.AvailableVersion,
            UpdateAvailable = snapshot.UpdateAvailable,
            IsConfigured = snapshot.IsConfigured,
            IsDesktop = snapshot.IsDesktop,
            CheckStatus = snapshot.CheckStatus switch
            {
                AppUpdateCheckStatus.NotChecked => "notChecked",
                AppUpdateCheckStatus.Ready => "ready",
                AppUpdateCheckStatus.Offline => "offline",
                AppUpdateCheckStatus.Failed => "failed",
                _ => "failed"
            },
            LastCheckedUtc = snapshot.LastCheckedUtc?.ToUnixTimeMilliseconds()
        };
    }
}
