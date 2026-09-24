namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class GetDevelopmentArtifactEndpoint : Endpoint<DevelopmentArtifactRequest, DevelopmentArtifactContentResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public GetDevelopmentArtifactEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentArtifactRequest req, CancellationToken ct)
    {
        var artifact = await _service.ReadArtifactAsync(req.ProjectId, req.TaskId, req.ArtifactId, ct);
        await Send.OkAsync(new DevelopmentArtifactContentResponse
        {
            Artifact = artifact.Artifact.ToResponse(),
            Content = artifact.Content
        }, ct);
    }
}
