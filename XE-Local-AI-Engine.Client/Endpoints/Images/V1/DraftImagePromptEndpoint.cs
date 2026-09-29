namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Drafts a text-to-image prompt from the operator's idea using a node-local chat model. Writes nothing: the response
///     fills the generation form, and <c>images/jobs</c> remains the only path that starts a job.
/// </summary>
public sealed class DraftImagePromptEndpoint : Endpoint<DraftImagePromptRequest, ImagePromptDraftResponse>
{
    private readonly IConfigDraftService _configDraftService;

    public DraftImagePromptEndpoint(IConfigDraftService configDraftService)
    {
        ArgumentNullException.ThrowIfNull(configDraftService);
        _configDraftService = configDraftService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.PromptDraft);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<DraftImagePromptRequest>("application/json")
                               .Produces<ImagePromptDraftResponse>(StatusCodes.Status200OK)
                               .Produces<DraftErrorResponse>(StatusCodes.Status409Conflict)
                               .Produces<DraftErrorResponse>(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(DraftImagePromptRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var result = await _configDraftService
            .DraftImagePromptAsync(new ConfigDraftRequest
                {
                    Mode = req.Mode,
                    ModelName = req.ModelName!,
                    Brief = req.Brief!,
                    ExistingDescription = req.ExistingNegativePrompt,
                    ExistingContent = req.ExistingPrompt
                },
                ct);

        if (result.Draft is not { } draft)
        {
            if (DraftEndpointSupport.ToTypedFailure(result) is { } typedFailure)
            {
                await Send.ResultAsync(typedFailure);
                return;
            }

            AddError(result.FailureMessage ?? "The draft request was rejected.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await Send.OkAsync(new ImagePromptDraftResponse
            {
                Prompt = draft.Content,
                NegativePrompt = draft.Description,
                GenerationMetadata = new GenerationMetadata
                {
                    Model = req.ModelName,
                    Mode = req.Mode,
                    UserBrief = req.Brief,
                    Rationale = draft.Rationale,
                    Assumptions = draft.Assumptions,
                    Confidence = draft.Confidence,
                    GeneratedAtUtc = draft.GeneratedAtUtc.ToUnixTimeMilliseconds(),
                    DraftContentHash = draft.ContentHash
                }
            },
            ct);
    }
}
