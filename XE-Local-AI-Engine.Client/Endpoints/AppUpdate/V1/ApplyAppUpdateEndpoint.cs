namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Operator-initiated apply of an available app update.
/// </summary>
/// <remarks>
///     Delegates to <see cref="IAppUpdateService.ApplyAsync" />, which downloads the latest release and schedules
///     Velopack to apply it after this host exits, a no-op when none is available. The endpoint completes its success
///     response before requesting graceful shutdown, so the browser can enter restart polling without mistaking
///     process exit for an apply failure. Apply failures surface as a sanitized 400.
/// </remarks>
public sealed class ApplyAppUpdateEndpoint : EndpointWithoutRequest<ApplyAppUpdateResponse>, IDesktopOnlyEndpoint
{
    private readonly IAppUpdateService _updateService;
    private readonly AppUpdateShutdownCoordinator _shutdownCoordinator;

    public ApplyAppUpdateEndpoint(
        IAppUpdateService updateService,
        AppUpdateShutdownCoordinator shutdownCoordinator)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        ArgumentNullException.ThrowIfNull(shutdownCoordinator);
        _updateService = updateService;
        _shutdownCoordinator = shutdownCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.AppUpdate.Apply);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Base `Applying` on the REAL apply outcome — the service live-re-checks GitHub — not a stale snapshot. When true Velopack waits for this process to exit, and
        // OnCompleted stops the host only after the JSON response is complete. An apply failure throws AppUpdateException, whose sanitized message (no local path or feed URL) becomes the 400.
        var applying = await _updateService.ApplyAsync(ct);
        if (applying)
        {
            _shutdownCoordinator.StopAfterResponseCompleted(HttpContext.Response);
        }

        await Send.OkAsync(new ApplyAppUpdateResponse
            {
                Applying = applying
            },
            ct);
    }
}
