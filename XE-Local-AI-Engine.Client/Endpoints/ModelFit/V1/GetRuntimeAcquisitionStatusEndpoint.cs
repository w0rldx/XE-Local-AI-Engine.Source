namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Read-only first-run llama.cpp runtime acquisition status (GET model-fit/llamacpp/acquisition): the current
///     phase, variant/tag, byte progress and archive step counter, as the one-shot hydrate on mount.
/// </summary>
/// <remarks>
///     <b>Zero side effects.</b> It only reads the administration service's current acquisition snapshot and must never
///     trigger an acquisition the way <see cref="EnsureLlamaCppBinaryEndpoint" /> does: the client hydrates this on
///     mount, so a multi-hundred-MB download started from a GET would fire on any fresh node a page loads on. Before
///     acquisition has ever run the registry serves an <c>Idle</c> snapshot at sequence 0. Live progress streams over
///     the acquisition hub — docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
/// </remarks>
public sealed class GetRuntimeAcquisitionStatusEndpoint : EndpointWithoutRequest<RuntimeAcquisitionStatusResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService;

    public GetRuntimeAcquisitionStatusEndpoint(ILlamaCppRuntimeAdministrationService administrationService)
    {
        ArgumentNullException.ThrowIfNull(administrationService);
        _administrationService = administrationService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.LlamaCppAcquisition);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_administrationService.GetAcquisitionStatus().ToResponse(), ct);
    }
}
