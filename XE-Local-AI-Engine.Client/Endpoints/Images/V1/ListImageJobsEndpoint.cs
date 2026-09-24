namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     The operator's image jobs, paged server-side so the page can reach rows older than the first one.
///     Operator-gated.
/// </summary>
/// <remarks>
///     Every row carries a decrypted prompt, so an unpaged list would grow without bound on every page load. Ordered
///     newest-first by the store and never re-sorted here. Thin transport over <see cref="IImageJobCoordinator" />.
/// </remarks>
public sealed class ListImageJobsEndpoint : Endpoint<ListImageJobsRequest, ListImageJobsResponse>
{
    /// <summary>The page size a caller that names none gets.</summary>
    private const int DefaultLimit = 50;

    private readonly IImageJobCoordinator _coordinator;

    public ListImageJobsEndpoint(IImageJobCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.Jobs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ListImageJobsResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(ListImageJobsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var page = await _coordinator.ListAsync(Math.Clamp(req.Limit ?? DefaultLimit, min: 1, ListImageJobsRequestValidator.MaxLimit),
            Math.Max(req.Offset ?? 0, val2: 0),
            ct);

        await Send.OkAsync(new ListImageJobsResponse
            {
                Items = [.. page.Items.Select(static j => j.ToResponse())],
                TotalCount = page.TotalCount
            },
            ct);
    }
}
