namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Read-only app self-update status (GET app-update/status): the running version, the available version (when newer),
///     whether an update is available, the GitHub auth state, and whether this build is desktop / was offline. Reads the
///     shared <see cref="IAppUpdateState" /> snapshot (computed at startup); <c>?refresh=true</c> forces a fresh GitHub
///     check, subject to a 60s rate-limit floor. Never returns the GitHub token.
/// </summary>
public sealed class GetAppUpdateStatusEndpoint(IAppUpdateState updateState, IAppUpdateService updateService)
    : Endpoint<GetAppUpdateStatusRequest, AppUpdateStatusResponse>, IDesktopOnlyEndpoint
{
    // Minimum spacing between live GitHub refreshes — a snapshot younger than this is served from cache even on
    // ?refresh=true, so a rapid refresh loop can't burn the GitHub rate budget.
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IAppUpdateState _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
    private readonly IAppUpdateService _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));

    public override void Configure()
    {
        Get(LocalApiRoutes.AppUpdate.Status);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetAppUpdateStatusRequest req, CancellationToken ct)
    {
        var current = _updateState.Current;

        // Honor ?refresh=true only when the cached snapshot is older than the floor (the service itself is offline- and
        // auth-failure-tolerant, so a refresh never throws here).
        var snapshot = (req.Refresh ?? false) && IsStale(current.LastCheckedUtc)
            ? await _updateService.CheckForUpdatesAsync(ct).ConfigureAwait(false)
            : current;

        await Send.OkAsync(ToResponse(snapshot), ct).ConfigureAwait(false);
    }

    private static bool IsStale(DateTimeOffset? checkedAtUtc)
    {
        return checkedAtUtc is not { } checkedAt || DateTimeOffset.UtcNow - checkedAt >= MinRefreshInterval;
    }

    private static AppUpdateStatusResponse ToResponse(AppUpdateSnapshot snapshot)
    {
        return new AppUpdateStatusResponse
        {
            CurrentVersion = snapshot.CurrentVersion,
            AvailableVersion = snapshot.AvailableVersion,
            UpdateAvailable = snapshot.UpdateAvailable,
            AuthState = AppUpdateAuthStateWire.Of(snapshot.AuthState),
            Login = snapshot.Login,
            IsDesktop = snapshot.IsDesktop,
            IsOffline = snapshot.IsOffline,
            LastCheckedUtc = snapshot.LastCheckedUtc?.ToUnixTimeMilliseconds()
        };
    }
}
