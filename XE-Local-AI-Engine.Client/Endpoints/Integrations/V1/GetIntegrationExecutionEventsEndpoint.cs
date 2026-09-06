namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     One execution's persisted timeline, read from the events table and never from the in-memory ring: the ring is
///     evictable and empty after a restart, and an operator opening a run from last week must still see what happened.
///     <para>
///         Operator-gated and deliberately not key-scoped, like the other admin execution routes. Its response items
///         are the SAME record the external recovery route returns, so the timeline an operator reads and the rows an
///         integrator polls cannot drift apart.
///     </para>
/// </summary>
public sealed class GetIntegrationExecutionEventsEndpoint(IntegrationExecutionQueryService executions)
    : Endpoint<ListIntegrationExecutionEventsRequest, ListIntegrationExecutionEventsResponse>
{
    private readonly IntegrationExecutionQueryService _executions = executions ?? throw new ArgumentNullException(nameof(executions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.ExecutionEvents);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListIntegrationExecutionEventsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var rows = await _executions.ListEventsAsync(Route<Guid>("executionId"),
                                        Math.Max(req.SinceSeq ?? 0, val2: 0),
                                        IntegrationEventPage.ClampLimit(req.Limit),
                                        ct)
                                    .ConfigureAwait(false);

        await Send.OkAsync(new ListIntegrationExecutionEventsResponse
            {
                Items = [.. rows.Select(IntegrationMapper.ToEventDto)]
            },
            ct).ConfigureAwait(false);
    }
}
