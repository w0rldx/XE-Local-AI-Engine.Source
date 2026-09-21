namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class GetDevelopmentTaskEndpoint : Endpoint<DevelopmentTaskRequest, DevelopmentTaskDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public GetDevelopmentTaskEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.TaskById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTaskRequest req, CancellationToken ct)
    {
        await Send.OkAsync((await _service.GetTaskAsync(req.ProjectId, req.TaskId, ct)).ToResponse(), ct);
    }
}
