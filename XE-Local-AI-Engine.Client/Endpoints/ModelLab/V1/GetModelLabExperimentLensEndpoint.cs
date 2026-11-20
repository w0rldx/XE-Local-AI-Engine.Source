namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using System.Globalization;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>404 for a run that recorded no lens (saved before contract 5, or a checkpoint too deep for one) or a steering row; 422 when the stored lines fail their check.</summary>
public sealed class GetModelLabExperimentLensEndpoint : Endpoint<ModelLabExperimentRouteRequest, ModelLabLensResponse>
{
    private readonly ModelLabExperimentService _experiments;

    public GetModelLabExperimentLensEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.ExperimentLens);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabLensResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound)
                               .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(ModelLabExperimentRouteRequest request, CancellationToken ct)
    {
        var lens = await _experiments.ReadLensAsync(request.ExperimentId, ct);
        switch (lens)
        {
            case { Status: ModelLabTraceReadStatus.Found, Summary: { } summary }:
                // The parse checked that every top id has a text.
                var texts = new Dictionary<int, string>();
                foreach (var record in lens.Records)
                {
                    foreach (var id in record.Ids.SelectMany(static ids => ids))
                    {
                        texts.TryAdd(id, record.Texts[id.ToString(CultureInfo.InvariantCulture)]!);
                    }
                }

                await Send.OkAsync(new ModelLabLensResponse
                {
                    Layers = summary.Layers,
                    TopTokens = summary.TopTokens,
                    TiedEmbeddings = summary.TiedEmbeddings,
                    VocabSize = summary.VocabSize,
                    Steps =
                    [
                        .. lens.Records.Select(record => new ModelLabLensStepResponse
                        {
                            Step = record.Step,
                            Blocks =
                            [
                                .. summary.Layers.Select((layer, block) => new ModelLabLensBlockResponse
                                {
                                    Layer = layer,
                                    Ids = record.Ids[block],
                                    Logprobs = record.Logprobs[block],
                                    ChosenLogprob = record.ChosenLogprobs[block],
                                    ChosenRank = record.ChosenRanks[block],
                                    Entropy = record.Entropies[block]
                                })
                            ]
                        })
                    ],
                    Texts =
                    [
                        .. texts.Select(static text => new ModelLabLensTextResponse
                        {
                            Id = text.Key,
                            Text = text.Value
                        })
                    ],
                    Truncated = lens.Footer?.Truncated ?? false,
                    DroppedSteps = lens.Footer?.Dropped ?? 0
                }, ct);
                break;
            case { Status: ModelLabTraceReadStatus.NotFound }:
                await Send.NotFoundAsync(ct);
                break;
            default:
                AddError("The stored logit lens could not be read or failed its integrity check.");
                await Send.ErrorsAsync(StatusCodes.Status422UnprocessableEntity, ct);
                break;
        }
    }
}
