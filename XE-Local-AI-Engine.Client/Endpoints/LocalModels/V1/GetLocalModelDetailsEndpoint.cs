namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;

/// <summary>
///     Reports one model's details.
/// </summary>
/// <remarks>
///     The provider routing behind "which model is this and what are its details" lives in
///     <see cref="ILocalModelDetailsResolver" />; this endpoint binds, delegates, and maps the one resolution it gets
///     back — a <see cref="LocalModelDetailsResolution.NoLocalDetails" /> is the single 404 for every branch that has
///     no local details to report.
/// </remarks>
public sealed class GetLocalModelDetailsEndpoint : Endpoint<GetLocalModelDetailsRequest, LocalModelDetailsResponse>
{
    private readonly ILocalModelDetailsResolver _detailsResolver;

    public GetLocalModelDetailsEndpoint(
        ILocalModelDetailsResolver detailsResolver)
    {
        ArgumentNullException.ThrowIfNull(detailsResolver);
        _detailsResolver = detailsResolver;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.ModelDetails);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetLocalModelDetailsRequest req, CancellationToken ct)
    {
        // Decode again here: GetLocalModelDetailsRequestValidator already ran the grammar over the decoded name, and
        // probing the same decoded name keeps "validated name == probed name" true. See ModelRouteName.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        var modelName = decodedModelName!.Trim();
        var resolution = await _detailsResolver.ResolveAsync(modelName, ct);

        switch (resolution)
        {
            case LocalModelDetailsResolution.External external:
                await Send.OkAsync(external.Registration.ToDetailsResponse(), ct);
                return;
            case LocalModelDetailsResolution.Gguf gguf:
                await Send.OkAsync(gguf.Descriptor.ToDetailsResponse(modelName, gguf.EffectiveContextTokens), ct);
                return;
            case LocalModelDetailsResolution.Ollama ollama:
                await Send.OkAsync(ollama.Details.ToResponse(modelName), ct);
                return;
            default:
                await Send.NotFoundAsync(ct);
                return;
        }
    }
}
