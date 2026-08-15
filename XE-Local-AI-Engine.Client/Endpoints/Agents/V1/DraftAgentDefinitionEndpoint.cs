namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     Drafts an agent definition from an operator description using a node-local model. Writes nothing: the response
///     populates the operator's form and the existing <c>agents</c> create/update routes remain the only path to the
///     database.
/// </summary>
public sealed class DraftAgentDefinitionEndpoint(IConfigDraftService configDraftService)
    : Endpoint<DraftAgentDefinitionRequest, AgentDraftResponse>
{
    private const int MaxExistingDescriptionLength = 2000;
    private const int MaxExistingNameLength = 120;

    private readonly IConfigDraftService _configDraftService = configDraftService ?? throw new ArgumentNullException(nameof(configDraftService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.Draft);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<DraftAgentDefinitionRequest>("application/json")
                               .Produces<AgentDraftResponse>(StatusCodes.Status200OK)
                               .Produces<DraftErrorResponse>(StatusCodes.Status409Conflict)
                               .Produces<DraftErrorResponse>(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(DraftAgentDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var validationError = DraftEndpointSupport.ValidateRequest(req.ModelName,
            req.Brief,
            req.ExistingName,
            req.ExistingDescription,
            req.ExistingContent,
            MaxExistingNameLength,
            MaxExistingDescriptionLength);

        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _configDraftService
                           .DraftAgentDefinitionAsync(new ConfigDraftRequest(req.Mode,
                                   req.ModelName!,
                                   req.Brief!,
                                   req.ExistingName,
                                   req.ExistingDescription,
                                   req.ExistingContent),
                               ct)
                           .ConfigureAwait(false);

        if (result.Draft is not { } draft)
        {
            if (DraftEndpointSupport.ToTypedFailure(result) is { } typedFailure)
            {
                await Send.ResultAsync(typedFailure).ConfigureAwait(false);
                return;
            }

            AddError(result.FailureMessage ?? "The draft request was rejected.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new AgentDraftResponse
            {
                Name = draft.Name,
                Description = draft.Description,
                Instructions = draft.Content,
                GenerationMetadata = new GenerationMetadata
                {
                    // Model/mode/brief are the request's own values echoed into the block so the save path carries the
                    // whole provenance in one opaque object; the rest is what the service produced and stamped.
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
            ct).ConfigureAwait(false);
    }
}
