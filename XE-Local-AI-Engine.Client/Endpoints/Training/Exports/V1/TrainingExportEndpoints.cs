namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1.Mappers;
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
        var start = await _exports.StartExportAsync(req.RunId, new TrainingExportRequest(kind, req.QuantType), ct);
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

public sealed class ListTrainingArtifactsEndpoint(ITrainingExportService exports)
    : Endpoint<TrainingRunArtifactsRequest, ListTrainingArtifactsResponse>
{
    private readonly ITrainingExportService _exports = exports ?? throw new ArgumentNullException(nameof(exports));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RunArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingRunArtifactsRequest req, CancellationToken ct)
    {
        var artifacts = await _exports.ListArtifactsAsync(req.RunId, ct);
        await Send.OkAsync(new ListTrainingArtifactsResponse
        {
            Items = artifacts.Select(item => item.ToResponse()).ToArray()
        }, ct);
    }
}

public sealed class GetTrainingArtifactEndpoint(ITrainingExportService exports)
    : Endpoint<TrainingArtifactByIdRequest, TrainingArtifactResponse>
{
    private readonly ITrainingExportService _exports = exports ?? throw new ArgumentNullException(nameof(exports));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingArtifactByIdRequest req, CancellationToken ct)
    {
        var artifact = await _exports.GetArtifactAsync(req.ArtifactId, ct);
        if (artifact is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(artifact.ToResponse(), ct);
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
        await _exports.DeleteArtifactAsync(req.ArtifactId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
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
        var result = await _exports.RunSmokeAsync(req.ArtifactId, ct);
        await Send.OkAsync(new TrainingArtifactSmokeResponse
        {
            SmokeState = result.State.ToString(),
            SmokeReason = result.Reason
        }, ct);
    }
}

/// <summary>Registers a smoke-passed, quality-approved artifact as a local model, with its training lineage attached.</summary>
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
        var modelName = await _promotion.PromoteAsync(req.ArtifactId, req.ModelName, ct);
        await Send.OkAsync(new PromoteTrainingArtifactResponse
        {
            ModelName = modelName
        }, ct);
    }
}

public sealed class DecideTrainingArtifactQualityEndpoint(IArtifactQualityService quality)
    : Endpoint<DecideArtifactQualityRequest, ArtifactQualityResponse>
{
    private readonly IArtifactQualityService _quality = quality ?? throw new ArgumentNullException(nameof(quality));

    public override void Configure()
    {
        Put(LocalApiRoutes.Training.ArtifactQuality);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DecideArtifactQualityRequest req, CancellationToken ct)
    {
        var artifact = await _quality.DecideAsync(req.ArtifactId, req.ComparisonId, req.ExpectedVersion.GetValueOrDefault(), ct);
        await Send.OkAsync(ToQualityResponse(artifact), ct);
    }

    internal static ArtifactQualityResponse ToQualityResponse(TrainingArtifactRecord artifact)
    {
        var decision = ArtifactQualityService.ReadDecision(artifact)
                       ?? throw new InvalidOperationException("The persisted artifact quality decision could not be read.");
        return new ArtifactQualityResponse
        {
            ArtifactId = artifact.Id,
            ComparisonId = decision.ComparisonId,
            ArtifactSha256 = decision.ArtifactSha256,
            Outcome = decision.Outcome.ToString(),
            FailureCodes = decision.FailureCodes,
            OverrideReason = decision.OverrideReason,
            DiscardedAtUtc = artifact.DiscardedAtUtc,
            DiscardReason = artifact.DiscardReason,
            DiscardCleanupPending = artifact.DiscardCleanupPending,
            Version = artifact.Version
        };
    }
}

public sealed class OverrideTrainingArtifactQualityEndpoint(IArtifactQualityService quality)
    : Endpoint<OverrideArtifactQualityRequest, ArtifactQualityResponse>
{
    private readonly IArtifactQualityService _quality = quality ?? throw new ArgumentNullException(nameof(quality));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactQualityOverride);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(OverrideArtifactQualityRequest req, CancellationToken ct)
    {
        var artifact = await _quality.OverrideAsync(req.ArtifactId, req.ExpectedVersion.GetValueOrDefault(), req.Reason, ct);
        await Send.OkAsync(DecideTrainingArtifactQualityEndpoint.ToQualityResponse(artifact), ct);
    }
}

public sealed class BeginTrainingArtifactQualityRevalidationEndpoint(IArtifactQualityService quality)
    : Endpoint<BeginArtifactQualityRevalidationRequest, ArtifactQualityResponse>
{
    private readonly IArtifactQualityService _quality = quality ?? throw new ArgumentNullException(nameof(quality));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactQualityRevalidation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(BeginArtifactQualityRevalidationRequest req, CancellationToken ct)
    {
        var artifact = await _quality.BeginRevalidationAsync(req.ArtifactId, req.ExpectedVersion.GetValueOrDefault(), ct);
        await Send.OkAsync(DecideTrainingArtifactQualityEndpoint.ToQualityResponse(artifact), ct);
    }
}

public sealed class DiscardTrainingArtifactQualityEndpoint(ITrainingExportService exports)
    : Endpoint<DiscardArtifactQualityRequest, ArtifactQualityResponse>
{
    private readonly ITrainingExportService _exports = exports ?? throw new ArgumentNullException(nameof(exports));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactQualityDiscard);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DiscardArtifactQualityRequest req, CancellationToken ct)
    {
        var artifact = await _exports.DiscardArtifactQualityAsync(req.ArtifactId, req.ExpectedVersion.GetValueOrDefault(), req.Reason, ct);
        await Send.OkAsync(DecideTrainingArtifactQualityEndpoint.ToQualityResponse(artifact), ct);
    }
}
