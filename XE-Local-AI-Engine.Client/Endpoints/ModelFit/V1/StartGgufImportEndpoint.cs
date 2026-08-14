namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class StartGgufImportEndpoint(IGgufImportTransactionCoordinator coordinator)
    : Endpoint<StartGgufImportRequest, GgufAcquisitionTicketResponse>, IDesktopOnlyEndpoint
{
    private readonly IGgufImportTransactionCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.Import);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(StartGgufImportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SourcePath)
            || string.IsNullOrWhiteSpace(req.PreviewToken)
            || string.IsNullOrWhiteSpace(req.ModelBaseName)
            || string.IsNullOrWhiteSpace(req.Quantization))
        {
            AddError("The source, preview token, model base name, and quantization are required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var ticket = await _coordinator.StartAsync(new StartGgufImportCommand(req.SourcePath,
                req.PreviewToken,
                req.ModelBaseName,
                req.Quantization), ct).ConfigureAwait(false);
            await Send.ResultAsync(Results.Accepted(value: new GgufAcquisitionTicketResponse
            {
                OperationId = ticket.OperationId,
                OperationKind = ticket.OperationKind,
                ModelName = ticket.ModelName
            })).ConfigureAwait(false);
        }
        catch (GgufImportApplicationException exception)
        {
            await Send.ResultAsync(GgufImportEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}
