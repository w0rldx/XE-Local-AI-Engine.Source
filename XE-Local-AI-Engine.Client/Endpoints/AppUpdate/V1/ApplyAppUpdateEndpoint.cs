namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Operator-initiated apply of an available app update.
/// </summary>
/// <remarks>
///     Delegates to <see cref="IAppUpdateService.ApplyAsync" />, which downloads the latest release and schedules
///     Velopack to apply it after this host exits, a no-op when none is available. Running work answers 409 with the
///     list unless <c>force</c> is set. The success response completes before graceful shutdown is requested, so the
///     browser can enter restart polling. Apply failures surface as a sanitized 400.
/// </remarks>
public sealed class ApplyAppUpdateEndpoint : Endpoint<ApplyAppUpdateRequest, ApplyAppUpdateResponse>, IDesktopOnlyEndpoint
{
    private readonly IAppUpdateService _updateService;
    private readonly AppUpdateShutdownCoordinator _shutdownCoordinator;

    public ApplyAppUpdateEndpoint(IAppUpdateService updateService,
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
        Description(builder => builder
                               .Produces<ApplyAppUpdateResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<ApplyAppUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ApplyAppUpdateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Base `Applying` on the REAL apply outcome — the service live-re-checks GitHub — not a stale snapshot. When true Velopack waits for this process to exit, and
        // OnCompleted stops the host only after the JSON response is complete. An apply failure throws AppUpdateException, whose sanitized message (no local path or feed URL) becomes the 400.
        var result = await _updateService.ApplyAsync(req.Force, ct);
        if (ToBlockedResponse(result) is { } blocked)
        {
            await Send.ResultAsync(Results.Conflict(blocked));
            return;
        }

        if (result.Applying)
        {
            _shutdownCoordinator.StopAfterResponseCompleted(HttpContext.Response);
        }

        await Send.OkAsync(ToResponse(result), ct);
    }

    /// <summary>The 409 body when the apply was held back for running work; null when it was not.</summary>
    internal static ApplyAppUpdateBlockedResponse? ToBlockedResponse(AppUpdateApplyResult result) =>
        result.BusyItems.Count == 0
            ? null
            : new ApplyAppUpdateBlockedResponse
            {
                BusyItems = [.. result.BusyItems.Select(ToResponse)],
                Message = "Updating restarts XE and stops the work that is running now."
            };

    internal static ApplyAppUpdateResponse ToResponse(AppUpdateApplyResult result) =>
        new()
        {
            Applying = result.Applying,
            TargetVersion = result.TargetVersion
        };

    private static AppUpdateBusyItemResponse ToResponse(AppUpdateBusyItem item) =>
        new()
        {
            Kind = JsonNamingPolicy.CamelCase.ConvertName(item.Kind.ToString()),
            DisplayName = item.DisplayName
        };
}
