namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentArtifactsEndpoint : Endpoint<DevelopmentTaskRequest, ListDevelopmentArtifactsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public ListDevelopmentArtifactsEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.TaskArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTaskRequest req, CancellationToken ct)
    {
        var artifacts = await _service.ListArtifactsAsync(req.ProjectId, req.TaskId, ct);
        await Send.OkAsync(new ListDevelopmentArtifactsResponse
        {
            Items = artifacts.Select(DevelopmentContractMapper.ToResponse).ToArray()
        }, ct);
    }
}
