namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

/// <summary>
///     Starts an export. 202 — the pipeline owns the work from here, and its phases arrive on the run hub. Every
///     refusal is decided before anything is written, so a 409 has left nothing behind.
/// </summary>
public sealed class StartTrainingExportEndpoint : Endpoint<StartTrainingExportRequest, TrainingExportAcceptedResponse>
{
    private readonly ITrainingExportService _exports;

    public StartTrainingExportEndpoint(ITrainingExportService exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        _exports = exports;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.RunExports);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<TrainingExportAcceptedResponse>(StatusCodes.Status202Accepted)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<TrainingExportBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartTrainingExportRequest req, CancellationToken ct)
    {
        // The validator has already refused a null kind; this is the shape the compiler needs, not a second policy.
        var kind = req.Kind ?? TrainingArtifactKind.MergedGguf;
        var quantization = kind == TrainingArtifactKind.MergedGguf
            ? TrainingExportQuantizations.TryNormalize(req.QuantType) ?? req.QuantType ?? string.Empty
            : TrainingExportQuantizations.Float16;
        var start = await _exports.StartExportAsync(req.RunId, new TrainingExportRequest { Kind = kind, QuantType = req.QuantType }, ct);
        if (start.Outcome == TrainingExportStartOutcome.Accepted)
        {
            await Send.ResultAsync(TypedResults.Accepted((string?)null,
                          new TrainingExportAcceptedResponse
                          {
                              RunId = req.RunId,
                              Kind = kind.ToString(),
                              QuantType = quantization
                          }));
            return;
        }

        // A busy GPU or a missing runtime are 409s: nothing about the REQUEST is wrong, and retrying it later works.
        // Everything else is the operator asking for something this run cannot produce, which is a 400.
        if (start.Outcome is TrainingExportStartOutcome.Busy or TrainingExportStartOutcome.RuntimeUnavailable)
        {
            await Send.ResultAsync(TypedResults.Conflict(new TrainingExportBlockedResponse
                      {
                          Reason = start.Outcome.ToString(),
                          Message = start.Reason ?? "The export cannot start right now."
                      }));
            return;
        }

        AddError(start.Reason ?? "The export request is not valid.");
        await Send.ErrorsAsync(cancellation: ct);
    }
}
