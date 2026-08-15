namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

/// <summary>
///     Starts an export. 202 — the pipeline owns the work from here, and its phases arrive on the run hub. Every
///     refusal is decided before anything is written, so a 409 has left nothing behind.
/// </summary>
public sealed class StartTrainingExportEndpoint(ITrainingExportService exports)
    : Endpoint<StartTrainingExportRequest, TrainingExportAcceptedResponse>
{
    private readonly ITrainingExportService _exports = exports ?? throw new ArgumentNullException(nameof(exports));

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
        var start = await _exports.StartExportAsync(req.RunId, new TrainingExportRequest(kind, req.QuantType), ct).ConfigureAwait(false);
        if (start.Outcome == TrainingExportStartOutcome.Accepted)
        {
            await Send.ResultAsync(TypedResults.Accepted((string?)null,
                             new TrainingExportAcceptedResponse
                             {
                                 RunId = req.RunId,
                                 Kind = kind.ToString(),
                                 QuantType = quantization
                             }))
                      .ConfigureAwait(false);
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
                      }))
                      .ConfigureAwait(false);
            return;
        }

        AddError(start.Reason ?? "The export request is not valid.");
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
    }
}

public sealed class ListTrainingArtifactsEndpoint(ITrainingRunStore store)
    : Endpoint<TrainingRunArtifactsRequest, ListTrainingArtifactsResponse>
{
    private readonly ITrainingRunStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RunArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingRunArtifactsRequest req, CancellationToken ct)
    {
        var artifacts = await _store.ListArtifactsAsync(req.RunId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListTrainingArtifactsResponse
        {
            Items = artifacts.Select(item => item.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}

public sealed class GetTrainingArtifactEndpoint(ITrainingRunStore store)
    : Endpoint<TrainingArtifactByIdRequest, TrainingArtifactResponse>
{
    private readonly ITrainingRunStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingArtifactByIdRequest req, CancellationToken ct)
    {
        var artifact = await _store.GetArtifactAsync(req.ArtifactId, ct).ConfigureAwait(false);
        if (artifact is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(artifact.ToResponse(), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Deletes a staged artifact. Refused with 409 once it has been promoted — the registry entry owns it now.
///     Routed through the export service rather than the store so the staged bytes go with the row; the store only
///     ever removes the row.
/// </summary>
public sealed class DeleteTrainingArtifactEndpoint(ITrainingExportService exports) : Endpoint<DeleteTrainingArtifactRequest>
{
    private readonly ITrainingExportService _exports = exports ?? throw new ArgumentNullException(nameof(exports));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteTrainingArtifactRequest req, CancellationToken ct)
    {
        try
        {
            await _exports.DeleteArtifactAsync(req.ArtifactId, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

/// <summary>Re-runs the smoke gate against an already-staged artifact and records the new verdict.</summary>
public sealed class RunTrainingArtifactSmokeEndpoint(ITrainingExportService exports)
    : Endpoint<TrainingArtifactByIdRequest, TrainingArtifactSmokeResponse>
{
    private readonly ITrainingExportService _exports = exports ?? throw new ArgumentNullException(nameof(exports));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactSmoke);
        Policies(NodeAuthorizationPolicies.Operator);
        // The artifact id is the whole request and it comes from the route, so this POST has no body. Without
        // declaring that, FastEndpoints requires a JSON body and a bodyless call is answered with 415.
        Description(builder => builder
                               .Accepts<TrainingArtifactByIdRequest>()
                               .Produces<TrainingArtifactSmokeResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(TrainingArtifactByIdRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _exports.RunSmokeAsync(req.ArtifactId, ct).ConfigureAwait(false);
            await Send.OkAsync(new TrainingArtifactSmokeResponse
            {
                SmokeState = result.State.ToString(),
                SmokeReason = result.Reason
            }, ct).ConfigureAwait(false);
        }
        catch (TrainingExportRejectedException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

/// <summary>Registers a smoke-passed artifact as a local model, with its training lineage attached.</summary>
public sealed class PromoteTrainingArtifactEndpoint(IArtifactPromotionService promotion)
    : Endpoint<PromoteTrainingArtifactRequest, PromoteTrainingArtifactResponse>
{
    private readonly IArtifactPromotionService _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactPromote);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<PromoteTrainingArtifactResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(PromoteTrainingArtifactRequest req, CancellationToken ct)
    {
        try
        {
            var modelName = await _promotion.PromoteAsync(req.ArtifactId, req.ModelName, ct).ConfigureAwait(false);
            await Send.OkAsync(new PromoteTrainingArtifactResponse
            {
                ModelName = modelName
            }, ct).ConfigureAwait(false);
        }
        catch (TrainingExportRejectedException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}
