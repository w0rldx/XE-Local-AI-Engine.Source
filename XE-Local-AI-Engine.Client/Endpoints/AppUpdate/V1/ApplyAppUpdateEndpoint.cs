namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Operator-initiated apply of an available app update (POST app-update/apply). Delegates to
///     <see cref="IAppUpdateService.ApplyAsync" />, which downloads the latest release, applies it, and relaunches into
///     the new version (no-op when none is available). On a relaunch the process is replaced, so a returned response means
///     either nothing was applied or the relaunch is imminent. Apply failures surface as a sanitized 400 (no token / path).
/// </summary>
public sealed class ApplyAppUpdateEndpoint(IAppUpdateService updateService)
    : EndpointWithoutRequest<ApplyAppUpdateResponse>, IDesktopOnlyEndpoint
{
    private readonly IAppUpdateService _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));

    public override void Configure()
    {
        Post(LocalApiRoutes.AppUpdate.Apply);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            // If apply found an update it relaunches and never returns; reaching here means nothing was applied. Base
            // `Applying` on the REAL apply outcome (the service live-re-checks GitHub), not a possibly-stale snapshot —
            // otherwise the client would wait for a relaunch that never happens.
            var applying = await _updateService.ApplyAsync(ct).ConfigureAwait(false);

            await Send.OkAsync(new ApplyAppUpdateResponse
                {
                    Applying = applying
                },
                ct).ConfigureAwait(false);
        }
        catch (AppUpdateException exception)
        {
            // Contractually sanitized message (no token / path / URL).
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
