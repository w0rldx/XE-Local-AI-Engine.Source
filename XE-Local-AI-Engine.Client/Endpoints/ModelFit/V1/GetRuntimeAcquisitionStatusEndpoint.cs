namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Read-only first-run llama.cpp runtime acquisition status (GET model-fit/llamacpp/acquisition): the current phase,
///     variant/tag, byte progress, and archive step counter. This is the one-shot hydrate on mount; live progress streams
///     over the acquisition hub. It exists because acquisition starts within seconds of boot, very likely before the React
///     app has authenticated and opened its hub connection — without this read the banner would never appear for precisely
///     the slow-first-run case the channel exists to explain.
/// </summary>
/// <remarks>
///     <b>Zero side effects.</b> This endpoint only reads the administration service's current acquisition snapshot. It must
///     never trigger an acquisition the way <see cref="EnsureLlamaCppBinaryEndpoint" /> does — the client polls/hydrates
///     this on mount, so starting a multi-hundred-MB download from a GET would kick one off on any fresh node the moment a
///     page loads. Before acquisition has ever run the registry serves an <c>Idle</c> snapshot at sequence 0.
/// </remarks>
public sealed class GetRuntimeAcquisitionStatusEndpoint(ILlamaCppRuntimeAdministrationService administrationService)
    : EndpointWithoutRequest<RuntimeAcquisitionStatusResponse>
{
    private readonly ILlamaCppRuntimeAdministrationService _administrationService = administrationService ?? throw new ArgumentNullException(nameof(administrationService));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.LlamaCppAcquisition);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_administrationService.GetAcquisitionStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
