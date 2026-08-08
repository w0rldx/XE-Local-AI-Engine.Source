namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     FastEndpoints handler that lists the installed image models — name, repo, family, kind, present weight parts and
///     total size (GET images/models). Reads the on-disk registry manifest; no absolute path is surfaced. Operator-gated.
/// </summary>
public sealed class ListImageModelsEndpoint(IImageModelRegistry registry)
    : EndpointWithoutRequest<ListImageModelsResponse>
{
    private readonly IImageModelRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.Models);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var models = await _registry.ListAsync(ct).ConfigureAwait(false);

        await Send.OkAsync(new ListImageModelsResponse
            {
                Items = [.. models.Select(static m => m.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
