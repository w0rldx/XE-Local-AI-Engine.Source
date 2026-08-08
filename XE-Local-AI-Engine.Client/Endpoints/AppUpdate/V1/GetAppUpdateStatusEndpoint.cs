namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Read-only app self-update status (GET app-update/status): the running version, the available version (when newer),
///     whether an update is available, and whether this build is configured / desktop, plus a sanitized check status. Reads the
///     shared <see cref="IAppUpdateState" /> snapshot (computed at startup); <c>?refresh=true</c> forces a fresh GitHub
///     check, subject to a 10-minute rate-limit floor.
/// </summary>
public sealed class GetAppUpdateStatusEndpoint(IAppUpdateState updateState, IAppUpdateService updateService)
    : Endpoint<GetAppUpdateStatusRequest, AppUpdateStatusResponse>, IDesktopOnlyEndpoint
{
    // Minimum spacing between anonymous live GitHub refreshes.
    internal static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly IAppUpdateState _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
    private readonly IAppUpdateService _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));

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
            ? await _updateService.RefreshIfStaleAsync(MinRefreshInterval, ct).ConfigureAwait(false)
            : _updateState.Current;

        await Send.OkAsync(ToResponse(snapshot), ct).ConfigureAwait(false);
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
