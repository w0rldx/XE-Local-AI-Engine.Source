namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Validators;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     The operator's sessions page, filtered and paged server-side so it can reach rows older than one page. Ordered
///     <c>LastActivityUtc</c> then <c>Id</c> descending by the store, and never re-sorted here.
/// </summary>
public sealed class ListIntegrationSessionsEndpoint(IntegrationSessionService sessions)
    : Endpoint<ListIntegrationSessionsRequest, ListIntegrationSessionsResponse>
{
    /// <summary>The page size a caller that names none gets.</summary>
    private const int DefaultLimit = 50;

    private readonly IntegrationSessionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.Sessions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListIntegrationSessionsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var rows = await _sessions.ListAsync(new IntegrationSessionFilter(req.TriggerId,
                                       req.Status,
                                       Math.Clamp(req.Limit ?? DefaultLimit, min: 1, ListIntegrationSessionsRequestValidator.MaxLimit),
                                       Math.Max(req.Offset ?? 0, val2: 0)),
                                   ct)
                                  .ConfigureAwait(false);

        await Send.OkAsync(new ListIntegrationSessionsResponse
            {
                Items = rows.Select(IntegrationMapper.ToResponse).ToArray()
            },
            ct).ConfigureAwait(false);
    }
}
