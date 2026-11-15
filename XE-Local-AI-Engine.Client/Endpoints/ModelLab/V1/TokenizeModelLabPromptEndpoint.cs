namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>A Next token run's rendered prompt as tokens. It never loads a model: one that is no longer running is a 409.</summary>
public sealed class TokenizeModelLabPromptEndpoint : Endpoint<ModelLabPromptTokensRequest, ModelLabPromptTokensResponse>
{
    private readonly IModelLabPromptTokensService _promptTokens;

    public TokenizeModelLabPromptEndpoint(IModelLabPromptTokensService promptTokens)
    {
        ArgumentNullException.ThrowIfNull(promptTokens);
        _promptTokens = promptTokens;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.PromptTokens);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabPromptTokensResponse>()
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces<ModelLabStartBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ModelLabPromptTokensRequest request, CancellationToken ct)
    {
        var tokens = await _promptTokens.GetPromptTokensAsync(new ModelLabPromptTokensQuery
        {
            ModelName = request.ModelName,
            SystemPrompt = request.SystemPrompt,
            Prompt = request.Prompt,
            DisableThinking = request.DisableThinking
        }, ct);
        if (tokens is null)
        {
            await Send.ResultAsync(ModelLabBlockedEndpointSupport.Blocked(ModelLabStartRefusal.ModelNotLoaded,
                "The model is no longer loaded. Run it again to see its prompt tokens."));
            return;
        }

        await Send.OkAsync(ModelLabEndpointMapper.ToResponse(tokens), ct);
    }
}
