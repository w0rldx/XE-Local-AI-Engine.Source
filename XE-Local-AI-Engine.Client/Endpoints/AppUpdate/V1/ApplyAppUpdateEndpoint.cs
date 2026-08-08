namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Operator-initiated apply of an available app update (POST app-update/apply). Delegates to
///     <see cref="IAppUpdateService.ApplyAsync" />, which downloads the latest release and schedules Velopack to apply it
///     after this host exits (no-op when none is available). The endpoint completes its success response before requesting
///     graceful shutdown, so the browser can enter restart polling without mistaking process exit for an apply failure.
///     Apply failures surface as a sanitized 400.
/// </summary>
public sealed class ApplyAppUpdateEndpoint(
    IAppUpdateService updateService,
    AppUpdateShutdownCoordinator shutdownCoordinator)
    : EndpointWithoutRequest<ApplyAppUpdateResponse>, IDesktopOnlyEndpoint
{
    private readonly IAppUpdateService _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));

    private readonly AppUpdateShutdownCoordinator _shutdownCoordinator = shutdownCoordinator
                                                                         ?? throw new ArgumentNullException(nameof(shutdownCoordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.AppUpdate.Apply);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            // Base `Applying` on the REAL apply outcome (the service live-re-checks GitHub), not a possibly-stale
            // snapshot. When true, Velopack is waiting for this process to exit; OnCompleted stops the host only after
            // the JSON response is complete, so the client reliably enters restart polling.
            var applying = await _updateService.ApplyAsync(ct).ConfigureAwait(false);
            if (applying)
            {
                _shutdownCoordinator.StopAfterResponseCompleted(HttpContext.Response);
            }

            await Send.OkAsync(new ApplyAppUpdateResponse
                {
                    Applying = applying
                },
                ct).ConfigureAwait(false);
        }
        catch (AppUpdateException exception)
        {
            // Contractually sanitized message (no local path or feed URL).
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
