namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Validators;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     The operator's execution history, filtered and paged server-side so it can reach rows older than one page.
///     Ordered newest first by the store, and never re-sorted here.
/// </summary>
public sealed class ListIntegrationExecutionsEndpoint(IntegrationExecutionQueryService executions)
    : Endpoint<ListIntegrationExecutionsRequest, ListIntegrationExecutionsResponse>
{
    /// <summary>The page size a caller that names none gets.</summary>
    private const int DefaultLimit = 50;

    private readonly IntegrationExecutionQueryService _executions = executions ?? throw new ArgumentNullException(nameof(executions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.Executions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListIntegrationExecutionsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var rows = await _executions.ListAsync(new IntegrationExecutionFilter(req.TriggerId,
                                           req.SessionId,
                                           req.Status,
                                           Math.Clamp(req.Limit ?? DefaultLimit, min: 1, ListIntegrationExecutionsRequestValidator.MaxLimit),
                                           Math.Max(req.Offset ?? 0, val2: 0)),
                                       ct)
                                   .ConfigureAwait(false);

        await Send.OkAsync(new ListIntegrationExecutionsResponse
            {
                Items = rows.Select(IntegrationMapper.ToSummary).ToArray()
            },
            ct).ConfigureAwait(false);
    }
}
