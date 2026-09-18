namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     One execution in full. Operator-gated and deliberately NOT key-scoped: an operator reading the admin surface is
///     not acting as an integrator and must be able to reach every row.
/// </summary>
public sealed class GetIntegrationExecutionEndpoint : EndpointWithoutRequest<IntegrationExecutionDetailDto>
{
    private readonly IntegrationExecutionQueryService _executions;

    public GetIntegrationExecutionEndpoint(IntegrationExecutionQueryService executions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        _executions = executions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Integrations.ExecutionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var execution = await _executions.GetAsync(Route<Guid>("executionId"), ct);
        if (execution is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(IntegrationMapper.ToDetail(execution), ct);
    }
}
